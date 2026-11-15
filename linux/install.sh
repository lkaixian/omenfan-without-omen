#!/bin/bash
# sudo ./install.sh : DKMS module (auto-rebuilt + signed on kernel updates), udev access, app, menu entry, load at boot.
set -e
[ "$EUID" = 0 ] || { echo "run with sudo"; exit 1; }
cd "$(dirname "$0")"
apt-get install -y dkms gir1.2-ayatanaappindicator3-0.1 python3-gi python3-gi-cairo gir1.2-gtk-3.0
rm -rf /usr/src/victusfan-1.0; mkdir /usr/src/victusfan-1.0
cp victusfan_wmi.c Makefile dkms.conf /usr/src/victusfan-1.0/
dkms remove victusfan/1.0 --all 2>/dev/null || true
dkms add victusfan/1.0; dkms build victusfan/1.0; dkms install victusfan/1.0 --force
echo victusfan_wmi > /etc/modules-load.d/victusfan.conf
rm -f /etc/udev/rules.d/99-victusfan.rules; cp 70-victusfan.rules /etc/udev/rules.d/; udevadm control --reload
mkdir -p /usr/local/lib/victusfan
cp victusfan.py victusfan_gui.py victusfan.svg /usr/local/lib/victusfan/
chmod +x /usr/local/lib/victusfan/*.py
ln -sf /usr/local/lib/victusfan/victusfan.py /usr/local/bin/victusfan
ln -sf /usr/local/lib/victusfan/victusfan_gui.py /usr/local/bin/victusfan-gui
install -D victusfan.svg /usr/share/icons/hicolor/scalable/apps/victusfan.svg
gtk-update-icon-cache -f /usr/share/icons/hicolor 2>/dev/null || true
cp victusfan.desktop /usr/share/applications/
modprobe -r victusfan_wmi 2>/dev/null || true
modprobe victusfan_wmi; udevadm trigger --subsystem-match=misc --action=change; udevadm settle
echo "Installed. Launch 'VictusFan' from the app menu (or victusfan-gui). Enable 'Start at login' inside the app."
