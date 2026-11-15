# VictusFan

Tiny fan control for HP Victus / Omen laptops, without Omen Gaming Hub.
One ~48 KB exe, no driver, no installer, no HP software.

- Live temperature and fan RPM
- Auto (BIOS) / custom fan curve / max fan
- Drag-to-edit fan curve, import / export
- Tray icon, start with Windows, dark mode

It calls the same BIOS WMI interface (`root\wmi`, `hpqBIntM.hpqBIOSInt128`) that Omen Gaming Hub uses.

## Use

1. Build (see below) or grab `VictusFan.exe` from the [Releases](../../releases) page.
2. Run it. It asks for admin rights (UAC); the BIOS calls need them.
3. Pick a mode. Close the window to keep it running in the tray.
   **Settings > Start with Windows** (also in the tray menu) adds a startup entry that shows up in Task Manager > Startup apps.
   It starts hidden in the tray with no UAC prompt, also on battery: the entry runs `VictusFan.exe --autostart`, which fires an
   elevated scheduled task. (Windows won't autostart an admin-only exe directly, so the exe starts un-elevated and re-launches itself.)

The BIOS drops manual fan settings after ~2 minutes, so the app must keep running for the curve to stay applied.

CLI: `VictusFan.exe status | probe | set <rpm> | max [off] | auto`
Run it from an **admin** terminal (otherwise it re-launches via UAC and the output is lost).
It is a GUI exe; pipe to see output: `.\VictusFan.exe status | Out-Host`.

The fan curve lives in `curve.txt` next to the exe (`temp_C rpm` per line, linear interpolation).

## Build

Windows only, no SDK needed. Uses the C# compiler that ships with Windows:

```powershell
.\build.ps1
```

## Tested on

| Model | BIOS | Result |
|---|---|---|
| Victus 16-e0xxx (Ryzen 5 5600H, GTX 1650) | F.26 | fan read/set, max fan work |

Other Victus/Omen models may differ. Reports welcome.

## Known limits

- **Temperature** comes from the Windows ACPI thermal zone, not the CPU die (the BIOS temperature command returns 0 on the tested model). It is smoothed with a 5-sample median.
- Auto mode hands control back by writing fan level 0 (as the Linux `hp-wmi` driver does). Unverified on every BIOS; if the fans linger, that is the BIOS timer.
- Manual fan control overrides the laptop's thermal management. **Use at your own risk.**

Not affiliated with HP.

## License

MIT
