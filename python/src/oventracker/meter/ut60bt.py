"""A UNI-T UT60BT over Bluetooth LE, with a type K thermocouple in its temperature mode (Meter/Ut60btMeter.cs and
Ut60btDriver.cs). Bluetooth LE goes through bleak, an optional extra (``pip install "./python[ble]"``), imported
only when the meter is used.

The meter needn't be paired: bleak finds it by scanning for its advertisements, on Windows, Linux (through BlueZ)
and macOS."""

from __future__ import annotations

import sys
import threading
import time
from datetime import datetime, timedelta, timezone

from . import ble
from . import ut60bt_protocol as protocol
from .auto_power_off import AutoPowerOff
from .base import MeterConfig, MeterIdentity, MeterReading, TemperatureMeter
from .driver import MeterCandidate, MeterDriver, MeterRequest, ProbeResult
from .resource_spec import ResourceSpec, Transport, describe
from .ut60bt_protocol import Ut60btAssembler, Ut60btFrame

DRIVER_NAME = "ut60bt"

#: The UT60BT switches off after 15 minutes without being used (its user manual).
DEFAULT_AUTO_POWER_OFF = timedelta(minutes=15)


class Ut60btMeter(TemperatureMeter):
    """A UNI-T UT60BT reached over Bluetooth LE. Unlike the 121GW it must be asked: the app sends it commands on one
    characteristic, and its frames come back as notifications on another.

    The sources differ on how to ask. libreble, from a real UT60BT, asks for its name first (it ignored requests for
    data until then) and then asks for data once, after which frames came a few times a second. dmmpy and
    unit_ut61eplus send a different command for each frame. So on connecting the app asks for the name, then asks
    for data whenever no frame has come for ``poll_every``, switching between the two commands while a request goes
    unanswered. Either kind of meter then works, and a stream that stalls is asked again.

    Like the 121GW, the newest frame is kept, and :meth:`read` waits for one newer than it last returned. Found and
    opened by :class:`Ut60btDriver`.

    ``timeout``: how long to wait for data before giving up, in seconds: when connecting, and for each reading.
    ``poll_every``: how long without a frame before asking again (the meter sends about three a second when
    streaming). ``name_wait``: how long to wait for its name on connecting, each time it's asked (three at most)."""

    def __init__(self, link: ble.BleLink, timeout: float = 6.0, poll_every: float = 0.7, name_wait: float = 0.6):
        self._link = link
        self.timeout = timeout
        self.poll_every = poll_every
        self.name_wait = name_wait
        self._gate = threading.Condition()
        self._assembler = Ut60btAssembler()
        self._requests = [protocol.GET_DATA, protocol.TRIGGER]
        self._latest: Ut60btFrame | None = None
        self._latest_time = datetime.now(timezone.utc)
        self._latest_mono = 0.0
        self._received = 0
        self._returned = 0
        self._name: str | None = None
        self._open = False
        # The poll: which request is next, when the last went, and how many frames had come by then.
        self._request = 0
        self._last_poll: float | None = None
        self._received_at_poll = -1

    @property
    def location(self) -> str:
        return "ble:" + self._link.address

    @property
    def frames_received(self) -> int:
        """How many data frames have arrived: for tests."""
        with self._gate:
            return self._received

    @property
    def device_name(self) -> str | None:
        """The name the meter gave when asked, e.g. "UT60BT"; None if it didn't answer."""
        with self._gate:
            return self._name

    def connect(self) -> MeterIdentity:
        self.disconnect()
        # Start clean: after a reconnect (the meter switched off and on, say) nothing from before counts. The meter
        # must be asked its name again, and a frame from before mustn't pass for one from now, or give its battery.
        with self._gate:
            self._assembler = Ut60btAssembler()
            self._latest = None
            self._latest_mono = 0.0
            self._received = 0
            self._returned = 0
            self._name = None
            self._request = 0
            self._last_poll = None
            self._received_at_poll = -1
        self._open = True  # before opening: a link that fails halfway is still closed
        self._link.open(self._on_data)

        for _ in range(3):
            if self.device_name is not None:
                break
            self._send(protocol.GET_NAME)
            until = time.monotonic() + self.name_wait
            with self._gate:
                while self._name is None and time.monotonic() < until:
                    self._gate.wait(until - time.monotonic())

        if self._next_frame(0, take=False) is None:
            raise TimeoutError("The UT60BT sent no data. Check it's on, and Bluetooth is on (hold SELECT until BT shows on its display).")
        return MeterIdentity("UNI-T", "UT60BT", "?", "?")

    def _on_data(self, data: bytes) -> None:
        with self._gate:
            for f in self._assembler.add(data):
                if protocol.is_data(f):
                    self._latest = Ut60btFrame(f)
                    self._latest_time = datetime.now(timezone.utc)
                    self._latest_mono = time.monotonic()
                    self._received += 1
                else:
                    name = protocol.name_in(f)
                    if name is not None:
                        self._name = name
            self._gate.notify_all()

    def _send(self, command: bytes) -> None:
        """Sends a command, ignoring a failure: an unanswered request is retried, and a link that's gone shows as no data."""
        try:
            self._link.write(command)
        except Exception:
            pass  # whatever the link raises, it shows as no data

    def _poll_if_quiet(self) -> None:
        """Asks for data, if no frame has come for a while and the last request has had its chance."""
        with self._gate:
            now = time.monotonic()
            quiet = self._received == 0 or now - self._latest_mono >= self.poll_every
            if not quiet or (self._last_poll is not None and now - self._last_poll < self.poll_every):
                return
            if self._received_at_poll == self._received:  # the last went unanswered
                self._request = (self._request + 1) % len(self._requests)
            self._received_at_poll = self._received
            self._last_poll = now
            command = self._requests[self._request]
        self._send(command)

    def _next_frame(self, after: int, take: bool) -> tuple[Ut60btFrame, datetime] | None:
        """The newest frame after the ``after``th one, with the time it came, asking for data while none comes; None
        if none comes within ``timeout``. With ``take``, it's marked as returned in the same lock, so a frame arriving
        just after can't be skipped (Ut60btMeter.NextFrame)."""
        deadline = time.monotonic() + self.timeout
        while True:
            self._poll_if_quiet()
            with self._gate:
                left = deadline - time.monotonic()
                if self._received <= after and left > 0:
                    self._gate.wait(min(left, self.poll_every))
                if self._received > after:
                    if take:
                        self._returned = self._received
                    assert self._latest is not None
                    return self._latest, self._latest_time
                if time.monotonic() >= deadline:
                    return None

    def disconnect(self) -> None:
        if self._open:
            self._link.dispose()
        self._open = False

    def read(self) -> MeterReading:
        with self._gate:
            seen = self._returned
        got = self._next_frame(seen, take=True)
        if got is None:
            raise TimeoutError("The UT60BT stopped sending (switched off, out of range, or Bluetooth turned off).")
        f, at = got
        return protocol.to_reading(at, f)

    def read_config(self) -> MeterConfig | None:
        with self._gate:
            f = self._latest
        return None if f is None else protocol.config(f)

    @property
    def last_config(self) -> MeterConfig | None:
        return self.read_config()

    def read_low_battery(self) -> bool | None:
        """Whether the meter shows its low-battery sign (from 4.2 V on its three AAA cells); None before the first
        frame. It has no battery level to report, so :meth:`read_battery_percent` stays unknown."""
        with self._gate:
            return None if self._latest is None else self._latest.low_battery

    def dispose(self) -> None:
        self.disconnect()


def is_ut60bt(d: ble.BleDevice) -> bool:
    return d.name.lower().startswith(protocol.ADVERTISED_NAME.lower())


def _named(devices: list[ble.BleDevice], name: str) -> ble.BleDevice | None:
    wanted = name.lower()
    return next((d for d in devices if d.name.lower() == wanted), None) or next(
        (d for d in devices if d.name.lower().startswith(wanted)), None
    )


def resolve(what: str, devices: list[ble.BleDevice] | None = None) -> ble.BleDevice | None:
    """A device by address, or by name (a UT60BT's name). An address needs no scan: bleak looks for the device
    itself when connecting. A name is looked up in ``devices``, or in a scan when they aren't given. On macOS,
    where devices have identifiers rather than addresses, the identifier a scan gave is taken as it is."""
    address = ble.normalize_address(what)
    if address is not None:
        known = next((d for d in devices or [] if d.address == address), None)
        return known or ble.BleDevice(protocol.ADVERTISED_NAME, address)
    if devices is None:
        devices = ble.scan()
    by_id = next((d for d in devices if d.address.lower() == what.strip().lower()), None)
    return by_id or _named(devices, what)


def check(resource: str) -> str | None:
    """Why a resource can't be a UT60BT's, or None if it can: it connects only by Bluetooth LE."""
    spec = ResourceSpec.parse(resource)
    if spec is None:
        return f'"{resource}" isn\'t a Bluetooth LE address the app understands (e.g. ble:C4:BE:84:12:34:56 or ble:UT60BT).'
    if spec.transport != Transport.Ble:
        return f"the UT60BT connects by Bluetooth LE (ble:<address>), not {describe(spec.transport)}."
    return None


def _probe(device: ble.BleDevice, keep_open: bool) -> ProbeResult:
    meter = Ut60btMeter(ble.link_for(device.address, protocol.SERVICE, protocol.NOTIFY, protocol.WRITE))
    try:
        identity = meter.connect()
        if keep_open:
            return ProbeResult(meter.location, identity, None, meter)
        meter.dispose()
        return ProbeResult(meter.location, identity, None)
    except Exception as ex:
        meter.dispose()
        return ProbeResult(meter.location, None, "no data (is Bluetooth on? Hold SELECT until BT shows)" if isinstance(ex, TimeoutError) else str(ex))


_BT_ON = (
    "Check the meter is on, with Bluetooth on (hold SELECT until BT shows on its display; Bluetooth is off each time "
    "it's switched on), and in range."
)


class Ut60btDriver(MeterDriver):
    """A UNI-T UT60BT over Bluetooth LE, with a type K thermocouple in its temperature mode. It finds a UT60BT by
    itself, by scanning for a few seconds."""

    name = DRIVER_NAME
    has_battery = True
    description = "UNI-T UT60BT over Bluetooth LE"
    resource_help = "a Bluetooth LE meter, by address or name, e.g. ble:C4:BE:84:12:34:56 or ble:UT60BT; found by itself if left out"

    @property
    def not_found_help(self) -> str:
        if not ble.is_supported():
            return ble.not_supported_help()
        if sys.platform.startswith("linux"):
            help_text = (
                f"{_BT_ON} Bluetooth LE goes through BlueZ, so it must be running (sudo systemctl enable --now bluetooth), "
                "with the adapter on (bluetoothctl power on). If BlueZ refuses access, add yourself to the bluetooth group "
                "(sudo usermod -aG bluetooth $USER), then log in again. WSL has no Bluetooth."
            )
        elif sys.platform == "darwin":
            help_text = f"{_BT_ON} The terminal needs permission to use Bluetooth (System Settings, Privacy & Security, Bluetooth)."
        else:
            help_text = f"{_BT_ON} It needn't be paired, but Bluetooth must be on in Windows settings."
        error = ble.scan_error()
        return help_text if error is None else f"{help_text} The scan failed: {error}"

    @property
    def not_temperature_help(self) -> str:
        return "Turn the dial to temperature (°C or °F; SELECT switches) with the type K thermocouple plugged in, and turn off HOLD, REL and MAX/MIN."

    _APO = AutoPowerOff(
        DEFAULT_AUTO_POWER_OFF,
        [
            "Turn the dial to OFF.",
            "Hold SELECT while turning the dial to temperature, and keep holding it for more than 2 seconds.",
            "Auto power-off then stays off until the meter is next switched off. Turn Bluetooth on again afterwards (hold SELECT until BT shows).",
        ],
        "UNI-T UT60EU/UT60BT User Manual, auto power-off and Bluetooth",
        how_to_wake="turn the dial to OFF and back, then hold SELECT until BT shows to turn Bluetooth on again",
    )

    @property
    def auto_power_off(self) -> AutoPowerOff:
        """The UT60BT doesn't say whether auto power-off is on, so the app always warns. It can only be turned off as
        the meter is switched on, and comes back the next time."""
        return self._APO

    def open(self, request: MeterRequest) -> ProbeResult | None:
        if not ble.is_supported():
            return ProbeResult(request.resource or "ble", None, ble.not_supported_help())
        if request.resource is not None:
            given = request.resource
            problem = check(given)
            if problem is not None:
                return ProbeResult(given, None, problem)
            spec = ResourceSpec.parse(given)
            assert spec is not None
            if ble.normalize_address(spec.address) is None and request.progress is not None:
                request.progress(f"{spec.address} (scanning for a few seconds)")
            device = resolve(spec.address)
            if device is None:
                return ProbeResult(given, None, f'no Bluetooth LE device called or at "{spec.address}" nearby. {self.not_found_help}')
            if request.progress is not None:
                request.progress(f"{device.name} {device.address}")
            return _probe(device, keep_open=True)

        # The meter that answered last time, then each UT60BT a scan finds. The scan takes a few seconds, so it's
        # left until the last meter hasn't answered.
        tried: set[str] = set()
        if request.preferred is not None:
            ps = ResourceSpec.parse(request.preferred)
            if ps is not None and ps.transport == Transport.Ble and ble.normalize_address(ps.address) is not None:
                last = resolve(ps.address, [])
                assert last is not None
                tried.add(last.address)
                if request.progress is not None:
                    request.progress(f"{last.name} {last.address} (Bluetooth LE can take a few seconds)")
                r = _probe(last, keep_open=True)
                if r.meter is not None:
                    return r
        if request.progress is not None:
            request.progress("a UT60BT nearby (scanning for a few seconds)")
        for d in (d for d in ble.scan() if is_ut60bt(d)):
            if d.address in tried:
                continue
            tried.add(d.address)
            if request.progress is not None:
                request.progress(f"{d.name} {d.address} (Bluetooth LE can take a few seconds)")
            r = _probe(d, keep_open=True)
            if r.meter is not None:
                return r
        return None

    def discover(self) -> list[MeterCandidate]:
        """The Bluetooth LE devices advertising nearby, from a scan of a few seconds: the UT60BTs first, then the
        others that give a name (nameless ones are left out). Empty without bleak or Bluetooth."""
        if not ble.is_supported():
            return []
        devices = [d for d in ble.scan() if d.name]
        devices.sort(key=lambda d: not is_ut60bt(d))
        return [
            MeterCandidate(
                "ble:" + d.address,
                d.name,
                "UT60BT nearby" if is_ut60bt(d) else "Bluetooth LE device nearby",
                is_ut60bt(d),
                "Bluetooth LE",
            )
            for d in devices
        ]

    def probe(self, resource: str) -> ProbeResult:
        if not ble.is_supported():
            return ProbeResult(resource, None, ble.not_supported_help())
        problem = check(resource)
        if problem is not None:
            return ProbeResult(resource, None, problem)
        spec = ResourceSpec.parse(resource)
        assert spec is not None
        device = resolve(spec.address)
        return ProbeResult(resource, None, "no such Bluetooth LE device nearby") if device is None else _probe(device, keep_open=False)
