#!/bin/bash
# ./build-deb.sh : builds victusfan_<ver>_all.deb in the current directory (no root needed).
set -e
cd "$(dirname "$0")"
VER=$(sed -n 's/^PACKAGE_VERSION="\(.*\)"/\1/p' dkms.conf)
R=$(mktemp -d); trap 'rm -rf "$R"' EXIT
install -D -m644 victusfan_wmi.c Makefile dkms.conf -t "$R/usr/src/victusfan-$VER"
install -D -m644 70-victusfan.rules -t "$R/usr/lib/udev/rules.d"
install -D -m644 victusfan.svg -t "$R/usr/share/icons/hicolor/scalable/apps"
install -D -m644 victusfan.desktop -t "$R/usr/share/applications"
sed -i 's#/usr/local/lib/victusfan#/usr/lib/victusfan#' "$R/usr/share/applications/victusfan.desktop"
install -D -m755 victusfan.py victusfan_gui.py -t "$R/usr/lib/victusfan"
install -D -m644 victusfan.svg -t "$R/usr/lib/victusfan"
mkdir -p "$R/usr/bin" "$R/etc/modules-load.d"
ln -s ../lib/victusfan/victusfan.py "$R/usr/bin/victusfan"
ln -s ../lib/victusfan/victusfan_gui.py "$R/usr/bin/victusfan-gui"
echo victusfan_wmi > "$R/etc/modules-load.d/victusfan.conf"
chmod -R g-w,o-w "$R"; mkdir "$R/DEBIAN"
cat > "$R/DEBIAN/control" <<C
Package: victusfan
Version: $VER
Architecture: all
Maintainer: lkaixian <lkaixian91@gmail.com>
Depends: dkms, python3, python3-gi, python3-gi-cairo, gir1.2-gtk-3.0, gir1.2-ayatanaappindicator3-0.1
Section: utils
Priority: optional
Homepage: https://github.com/lkaixian/omenfan-without-omen
Description: Fan control for HP Victus / Omen laptops (no Omen Gaming Hub)
 Tray app with custom fan curve, Auto/Max modes, import/export and start at login.
 Ships a small DKMS kernel module that forwards the HP BIOS WMI fan calls.
 Manual fan control overrides the laptop's thermal management: use at your own risk.
C
cat > "$R/DEBIAN/postinst" <<P
#!/bin/sh
set -e
if [ "\$1" = configure ]; then
    # leftovers of the manual install.sh (older versions / old paths)
    for v in 1.0 0.1.0; do
        [ "\$v" = $VER ] || { dkms remove victusfan/\$v --all >/dev/null 2>&1 || true; rm -rf /usr/src/victusfan-\$v; }
    done
    rm -f /etc/udev/rules.d/70-victusfan.rules /etc/udev/rules.d/99-victusfan.rules /usr/local/bin/victusfan /usr/local/bin/victusfan-gui
    rm -rf /usr/local/lib/victusfan
    dkms remove victusfan/$VER --all >/dev/null 2>&1 || true
    dkms add victusfan/$VER
    dkms build victusfan/$VER
    dkms install victusfan/$VER --force
    modprobe -r victusfan_wmi 2>/dev/null || true
    modprobe victusfan_wmi || echo "victusfan: module not loaded (Secure Boot key not enrolled yet? see 'dmesg | tail')" >&2
    udevadm control --reload 2>/dev/null || true
    udevadm trigger --subsystem-match=misc --action=change 2>/dev/null || true
    gtk-update-icon-cache -f /usr/share/icons/hicolor 2>/dev/null || true
fi
P
cat > "$R/DEBIAN/prerm" <<P
#!/bin/sh
set -e
if [ "\$1" = remove ] || [ "\$1" = upgrade ] || [ "\$1" = deconfigure ]; then
    modprobe -r victusfan_wmi 2>/dev/null || true
    dkms remove victusfan/$VER --all >/dev/null 2>&1 || true
fi
P
chmod 755 "$R/DEBIAN/postinst" "$R/DEBIAN/prerm"
dpkg-deb --root-owner-group --build "$R" "victusfan_${VER}_all.deb" >/dev/null
echo "built victusfan_${VER}_all.deb"
