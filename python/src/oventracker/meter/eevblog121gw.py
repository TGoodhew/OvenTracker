"""An EEVblog 121GW over Bluetooth LE, with a type K thermocouple in its temperature mode (Meter/Eevblog121GwMeter.cs
and Eevblog121GwDriver.cs). Bluetooth LE goes through bleak, an optional extra (``pip install "./python[ble]"``),
imported only when the meter is used.

The meter needn't be paired: bleak finds it by scanning for its advertisements, on Windows, Linux (through BlueZ)
and macOS."""

from __future__ import annotations

import sys
import threading
import time
from datetime import datetime, timedelta, timezone

from . import ble
from . import eevblog121gw_protocol as protocol
from .auto_power_off import AutoPowerOff, AutoPowerOffSetting
from .base import MeterConfig, MeterIdentity, MeterReading, TemperatureMeter
from .driver import MeterCandidate, MeterDriver, MeterRequest, ProbeResult
from .eevblog121gw_protocol import Gw121Assembler, Gw121Packet
from .resource_spec import ResourceSpec, Transport, describe

DRIVER_NAME = "121gw"

#: The 121GW's auto power-off from the factory: 30 minutes (its manual, as relayed by the dmm-tools write-up).
DEFAULT_AUTO_POWER_OFF = timedelta(minutes=30)


class Eevblog121GwMeter(TemperatureMeter):
    """An EEVblog 121GW reached over Bluetooth LE. The meter streams its display about twice a second without being
    asked. The app keeps the latest packet, and :meth:`read` waits for a newer one than it last returned, so a slow
    stream never gives the same reading twice. Found and opened by :class:`Eevblog121GwDriver`.

    ``timeout``: how long to wait for data before giving up, in seconds: when connecting, and for each reading."""

    def __init__(self, link: ble.BleLink, timeout: float = 6.0):
        self._link = link
        self.timeout = timeout
        self._gate = threading.Condition()
        self._assembler = Gw121Assembler()
        self._latest: Gw121Packet | None = None
        self._latest_time = datetime.now(timezone.utc)
        self._received = 0
        self._returned = 0
        self._open = False

    @property
    def location(self) -> str:
        return "ble:" + self._link.address

    @property
    def packets_received(self) -> int:
        """How many packets have arrived: for tests and the status line."""
        with self._gate:
            return self._received

    def connect(self) -> MeterIdentity:
        self.disconnect()
        self._open = True  # before opening: a link that fails halfway is still closed
        self._link.open(self._on_data)
        got = self._wait_for_packet(0)
        if got is None:
            raise TimeoutError("The 121GW sent no data. Check it's on, and Bluetooth is on (BT on its display).")
        return MeterIdentity("EEVblog", "121GW", got[0].serial, "?")

    def _on_data(self, data: bytes) -> None:
        with self._gate:
            for p in self._assembler.add(data):
                self._latest = p
                self._latest_time = datetime.now(timezone.utc)
                self._received += 1
            self._gate.notify_all()

    def _wait_for_packet(self, after: int, take: bool = False) -> tuple[Gw121Packet, datetime] | None:
        """The newest packet after the ``after``th one, with the time it came, or None if none comes in time. With
        ``take``, it's marked as returned in the same lock: a packet arriving just after can't be skipped, or give
        its time to this one (Eevblog121GwMeter.WaitForPacket)."""
        deadline = time.monotonic() + self.timeout
        with self._gate:
            while self._received <= after:
                left = deadline - time.monotonic()
                if left <= 0:
                    return None
                self._gate.wait(left)
            if take:
                self._returned = self._received
            assert self._latest is not None
            return self._latest, self._latest_time

    def disconnect(self) -> None:
        if self._open:
            self._link.dispose()
        self._open = False

    def read(self) -> MeterReading:
        with self._gate:
            seen = self._returned
        got = self._wait_for_packet(seen, take=True)
        if got is None:
            raise TimeoutError("The 121GW stopped sending (switched off, out of range, or Bluetooth turned off).")
        p, at = got
        return protocol.to_reading(at, p)

    def read_config(self) -> MeterConfig | None:
        with self._gate:
            p = self._latest
        return None if p is None else protocol.config(p)

    @property
    def last_config(self) -> MeterConfig | None:
        return self.read_config()

    def read_auto_power_off(self) -> AutoPowerOffSetting:
        """The meter says whether auto power-off is armed (the APO icon); not how long it's set to."""
        with self._gate:
            p = self._latest
        if p is None:
            return AutoPowerOffSetting.UNKNOWN
        return AutoPowerOffSetting.on(DEFAULT_AUTO_POWER_OFF) if p.auto_power_off_armed else AutoPowerOffSetting.OFF

    @property
    def low_battery(self) -> bool:
        """Whether the meter shows its low-battery icon. The meter has no battery level to report, so
        :meth:`read_battery_percent` stays unknown."""
        with self._gate:
            return self._latest is not None and self._latest.low_battery

    def read_low_battery(self) -> bool | None:
        """The low-battery icon from the latest packet; None before the first one."""
        with self._gate:
            return None if self._latest is None else self._latest.low_battery

    def dispose(self) -> None:
        self.disconnect()


def _is_121gw(d: ble.BleDevice) -> bool:
    return d.name.lower().startswith(protocol.ADVERTISED_NAME.lower())


def _named(devices: list[ble.BleDevice], name: str) -> ble.BleDevice | None:
    wanted = name.lower()
    return next((d for d in devices if d.name.lower() == wanted), None) or next(
        (d for d in devices if d.name.lower().startswith(wanted)), None
    )


def resolve(what: str, devices: list[ble.BleDevice] | None = None) -> ble.BleDevice | None:
    """A device by address, or by name (a 121GW's name). An address needs no scan: bleak looks for the device
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
    """Why a resource can't be a 121GW's, or None if it can: it connects only by Bluetooth LE."""
    spec = ResourceSpec.parse(resource)
    if spec is None:
        return f'"{resource}" isn\'t a Bluetooth LE address the app understands (e.g. ble:C4:BE:84:12:34:56 or ble:121GW).'
    if spec.transport != Transport.Ble:
        return f"the 121GW connects by Bluetooth LE (ble:<address>), not {describe(spec.transport)}."
    return None


def _probe(device: ble.BleDevice, keep_open: bool) -> ProbeResult:
    meter = Eevblog121GwMeter(ble.link_for(device.address, protocol.SERVICE, protocol.CHARACTERISTIC))
    try:
        identity = meter.connect()
        if keep_open:
            return ProbeResult(meter.location, identity, None, meter)
        meter.dispose()
        return ProbeResult(meter.location, identity, None)
    except Exception as ex:
        meter.dispose()
        return ProbeResult(meter.location, None, "no data (is Bluetooth on? BT shows on the display)" if isinstance(ex, TimeoutError) else str(ex))


_BT_ON = "Check the meter is on, with Bluetooth on (BT shows on its display; holding the 1ms PEAK button turns Bluetooth on or off), and in range."


class Eevblog121GwDriver(MeterDriver):
    """An EEVblog 121GW over Bluetooth LE, with a type K thermocouple in its temperature mode. It finds a 121GW by
    itself, by scanning for a few seconds."""

    name = DRIVER_NAME
    has_battery = True
    description = "EEVblog 121GW over Bluetooth LE"
    resource_help = "a Bluetooth LE meter, by address or name, e.g. ble:C4:BE:84:12:34:56 or ble:121GW; found by itself if left out"

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
        return "Turn the dial to temperature with the thermocouple plugged in, and turn off HOLD, REL and MIN/MAX."

    _APO = AutoPowerOff(
        DEFAULT_AUTO_POWER_OFF,
        [
            "Press SETUP on the meter to enter its setup menu.",
            "Step through the items until the display shows APO.oN.",
            "Change it to APO.oF, then leave setup.",
            "If that doesn't work on your meter, see the auto power-off section of the 121GW manual. Logging to the SD card also keeps it on.",
        ],
        "EEVblog 121GW manual, auto power-off (as relayed by the dmm-tools 121GW write-up; the steps weren't checked against the manual)",
        how_to_wake="turn the dial, or press a button, then turn Bluetooth on again if BT isn't shown",
    )

    @property
    def auto_power_off(self) -> AutoPowerOff:
        """The 121GW reports whether auto power-off is armed (the APO icon, in every packet), so the app warns only
        when it is. The setup item shows as APO.oN or APO.oF. The button presses weren't checked against the manual."""
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

        # The meter that answered last time, then each 121GW a scan finds. The scan takes a few seconds, so it's
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
            request.progress("a 121GW nearby (scanning for a few seconds)")
        for d in (d for d in ble.scan() if _is_121gw(d)):
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
        """The Bluetooth LE devices advertising nearby, from a scan of a few seconds: the 121GWs first, then the
        others that give a name (nameless ones are left out). Empty without bleak or Bluetooth."""
        if not ble.is_supported():
            return []
        devices = [d for d in ble.scan() if d.name]
        devices.sort(key=lambda d: not _is_121gw(d))
        return [
            MeterCandidate(
                "ble:" + d.address,
                d.name,
                "121GW nearby" if _is_121gw(d) else "Bluetooth LE device nearby",
                _is_121gw(d),
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
