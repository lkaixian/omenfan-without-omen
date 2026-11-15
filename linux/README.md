# VictusFan for Linux

Same features as the Windows app: live temperature and fan RPM, Auto (BIOS) / custom fan curve / max fan,
drag-to-edit curve, import / export, tray icon, start at login, dark mode.

Linux's `hp-wmi` driver only exposes max/auto, so `victusfan_wmi.c` is a tiny module that passes the same
BIOS WMI call as the Windows exe through `/dev/victusfan`. The BIOS also ignores manual fan speeds unless
the "user-defined trigger" call (command 0x10) comes first, which `victusfan.py` does (as `hp-wmi` does).

## Install (Ubuntu, GNOME)

    cd linux && sudo ./install.sh

Installs the module through DKMS (rebuilt and signed on kernel updates, using the Secure Boot MOK key DKMS already
created at `/var/lib/shim-signed/mok`), a udev rule so your desktop user needs no root, and the app. Launch
**VictusFan** from the menu. The tray icon needs the AppIndicator extension (on by default on Ubuntu).

CLI: `victusfan status | set <rpm> | max [off] | auto | run`

Curve and settings live in `~/.config/victusfan/`.

## Tested on

| Model | Kernel | Result |
|---|---|---|
| Victus 16-e0xxx (Ryzen 5 5600H) | 7.0.0 (Ubuntu 24.04, GNOME/Wayland, Secure Boot on) | set rpm, max, auto, curve, tray, autostart |

Not affiliated with HP. Manual fan control overrides thermal management: use at your own risk.
