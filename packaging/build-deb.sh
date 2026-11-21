#!/usr/bin/env bash
# Builds OvenTracker's Debian packages (.deb, as Ubuntu, Debian and Raspberry Pi OS use) into artifacts/.
#
#   packaging/build-deb.sh dotnet <version> <linux-x64|linux-arm64> <published self-contained app dir>
#       oventracker_<version>_<amd64|arm64>.deb: the .NET app in /opt/oventracker, run as `oventracker`.
#       Self-contained, so it needs no .NET packages (Raspberry Pi OS has none). The app uses invariant
#       globalization, so it needs no ICU either: just the C and C++ runtime libraries and zlib.
#
#   packaging/build-deb.sh python <version> <oventracker-<version>-py3-none-any.whl>
#       oventracker-py_<version>_all.deb: the Python version, run as `oventracker-py`, using Ubuntu's own
#       python3-rich and python3-serial. (Ubuntu doesn't allow a system-wide pip install, so this is how it
#       installs there.) bleak and PyVISA are suggested: Bluetooth LE meters need bleak 3.0 or later, which
#       older Ubuntu releases don't package; there, use pipx with the wheel instead.
#
# Both install system-wide, side by side, with a menu entry (a terminal) and the icon. Needs dpkg-deb and python3.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
out="$root/artifacts"
kind="${1:?dotnet or python}"
version="${2:?version}"
maintainer="The Schnauzer Group LLC (https://github.com/TGoodhew/OvenTracker/issues)"
homepage="https://github.com/TGoodhew/OvenTracker"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
chmod 0755 "$stage"

# The icon, at the sizes desktops look for, and the scalable source.
icons() {
  local name="$1" size
  for size in 16 32 48 64 128 256 512; do
    install -Dm0644 "$here/icon/oventracker-$size.png" "$stage/usr/share/icons/hicolor/${size}x${size}/apps/$name.png"
  done
  install -Dm0644 "$here/icon/oventracker.svg" "$stage/usr/share/icons/hicolor/scalable/apps/$name.svg"
}

# A menu entry: the app runs in a terminal, showing its help.
desktop() {
  local name="$1" title="$2" exec="$3"
  install -d "$stage/usr/share/applications"
  cat > "$stage/usr/share/applications/$name.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$title
Comment=Measure an oven's temperature accuracy with a connected multimeter
Exec=sh -c '$exec --help; exec "\${SHELL:-sh}"'
Icon=$name
Terminal=true
Categories=Utility;Science;
EOF
}

copyright() {
  local name="$1"
  install -Dm0644 "$root/LICENSE" "$stage/usr/share/doc/$name/copyright"
}

control() {
  local name="$1" arch="$2" depends="$3" extra="$4" description="$5"
  install -d "$stage/DEBIAN"
  {
    echo "Package: $name"
    echo "Version: $version"
    echo "Architecture: $arch"
    echo "Maintainer: $maintainer"
    echo "Installed-Size: $(du -sk --exclude=DEBIAN "$stage" | cut -f1)"
    echo "Depends: $depends"
    [ -n "$extra" ] && echo "$extra"
    echo "Section: science"
    echo "Priority: optional"
    echo "Homepage: $homepage"
    echo "Description: $description"
    echo " Checks how accurate an oven's temperature setting really is, in bake and"
    echo " convection, with a connected multimeter and a type K thermocouple (a Keysight"
    echo " U1253B, an EEVblog 121GW, a UNI-T UT60BT or an HP 34401A), and recommends the"
    echo " oven's adjustment."
  } > "$stage/DEBIAN/control"
}

build() {
  local file="$out/$1"
  mkdir -p "$out"
  rm -f "$file"
  dpkg-deb --root-owner-group -Zxz --build "$stage" "$file" >/dev/null
  echo "   $file ($(du -h "$file" | cut -f1))"
}

case "$kind" in
  dotnet)
    rid="${3:?runtime: linux-x64 or linux-arm64}"
    app="${4:?the published app directory}"
    case "$rid" in
      linux-x64) arch=amd64 ;;
      linux-arm64) arch=arm64 ;;
      *) echo "Unknown runtime $rid" >&2; exit 1 ;;
    esac
    [ -x "$app/OvenTracker" ] || { echo "No OvenTracker program in $app" >&2; exit 1; }
    [ -f "$app/libcoreclr.so" ] || { echo "$app isn't a self-contained build (publish with --self-contained)" >&2; exit 1; }
    echo "==> Building oventracker_${version}_${arch}.deb"
    install -d "$stage/opt/oventracker" "$stage/usr/bin"
    cp -a "$app/." "$stage/opt/oventracker/"
    find "$stage/opt/oventracker" -type d -exec chmod 0755 {} +
    find "$stage/opt/oventracker" -type f -exec chmod 0644 {} +
    chmod 0755 "$stage/opt/oventracker/OvenTracker"
    if [ -f "$stage/opt/oventracker/createdump" ]; then chmod 0755 "$stage/opt/oventracker/createdump"; fi
    ln -s /opt/oventracker/OvenTracker "$stage/usr/bin/oventracker"
    icons oventracker
    desktop oventracker "OvenTracker" oventracker
    copyright oventracker
    control oventracker "$arch" "libc6, libgcc-s1, libstdc++6, zlib1g" "" \
      "measure an oven's temperature accuracy with a multimeter"
    build "oventracker_${version}_${arch}.deb"
    ;;

  python)
    wheel="${3:?the wheel}"
    [ -f "$wheel" ] || { echo "No wheel at $wheel" >&2; exit 1; }
    echo "==> Building oventracker-py_${version}_all.deb"
    site="$stage/usr/lib/python3/dist-packages"
    install -d "$site" "$stage/usr/bin"
    python3 -m zipfile -e "$wheel" "$site" # a wheel is a zip of what goes in site-packages
    find "$site" -type d -exec chmod 0755 {} +
    find "$site" -type f -exec chmod 0644 {} +
    # The command pip would make from the wheel's entry point (oventracker-py = oventracker.cli:main).
    cat > "$stage/usr/bin/oventracker-py" <<'EOF'
#!/usr/bin/python3
import sys

from oventracker.cli import main

sys.exit(main())
EOF
    chmod 0755 "$stage/usr/bin/oventracker-py"
    icons oventracker-py
    desktop oventracker-py "OvenTracker (Python)" oventracker-py
    copyright oventracker-py
    control oventracker-py all "python3 (>= 3.11), python3-rich (>= 13), python3-serial (>= 3.5)" \
      "Suggests: python3-bleak (>= 3.0), python3-pyvisa" \
      "measure an oven's temperature accuracy with a multimeter (Python)"
    build "oventracker-py_${version}_all.deb"
    ;;

  *)
    echo "Usage: $0 dotnet <version> <rid> <app dir> | python <version> <wheel>" >&2
    exit 1
    ;;
esac
