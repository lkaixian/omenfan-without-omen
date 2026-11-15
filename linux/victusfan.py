#!/usr/bin/env python3
"""VictusFan for Linux: status | set <rpm> | max [off] | auto | run. Needs root + victusfan_wmi.ko loaded."""
import glob, os, sys, time, signal

DEV = "/dev/victusfan"
CURVE = os.environ.get("VF_CURVE") or os.path.expanduser("~/.config/victusfan/curve.txt")
DEFAULT = [(40, 2200), (60, 3200), (75, 4500), (85, 5800)]
FAN_COUNT, FAN_GET, FAN_SET, FAN_MAX = 0x10, 0x2D, 0x2E, 0x27

def call(cmd, *data):
    with open(DEV, "r+b", buffering=0) as f:
        f.write(" ".join([f"{cmd:x}", *(f"{b:x}" for b in data)]).encode())
        r = f.read(600).split()
    if r[0] != b"0":
        raise RuntimeError(f"BIOS code {r[0].decode()} for cmd {cmd:#x}")
    return bytes.fromhex(b"".join(r[1:]).decode()) if len(r) > 1 else b""

def fans():
    d = call(FAN_GET, 0, 0, 0, 0)
    return d[0] * 100, d[1] * 100

def set_rpm(rpm):
    b = max(0, min(65, rpm // 100))  # clamp 6500 rpm
    call(FAN_COUNT, 0, 0, 0, 0)  # "user-defined trigger": hp-wmi calls it first, else the BIOS ignores 0x2E
    call(FAN_MAX, 0)  # max must be explicitly off
    call(FAN_SET, b, b)

def set_max(on):
    call(FAN_MAX, int(on))

def release():  # level 0 = BIOS auto (as hp-wmi does)
    set_max(False); set_rpm(0)

def temp():  # CPU die temp in C
    for h in glob.glob("/sys/class/hwmon/hwmon*"):
        if open(h + "/name").read().strip() == "k10temp":
            return int(open(h + "/temp1_input").read()) // 1000
    return int(open("/sys/class/thermal/thermal_zone0/temp").read()) // 1000

def load_curve(path=CURVE):
    pts = []
    try:
        for l in open(path):
            s = l.split("#")[0].split()
            if len(s) >= 2: pts.append((int(s[0]), int(s[1])))
    except (OSError, ValueError):
        pts = []
    return sorted(pts) if len(pts) >= 2 else DEFAULT

def save_curve(pts, path=CURVE):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write("# temp_C  fan_rpm   (linear interpolation; the GUI edits this file)\n")
        f.writelines(f"{t} {r}\n" for t, r in pts)

def curve_rpm(pts, t):
    if t <= pts[0][0]: return pts[0][1]
    for (t0, r0), (t1, r1) in zip(pts, pts[1:]):
        if t <= t1: return r0 + (r1 - r0) * (t - t0) // (t1 - t0)
    return pts[-1][1]

def run(interval=2):
    hist = []
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    try:
        set_max(False)
        while True:  # BIOS drops manual settings after ~2 min, so re-apply every tick
            hist = (hist + [temp()])[-5:]
            t = sorted(hist)[len(hist) // 2]  # 5-sample median
            set_rpm(curve_rpm(load_curve(), t))
            time.sleep(interval)
    finally:
        release()

def main(a):
    cmd = a[0] if a else "status"
    if cmd == "status":
        print(f"temp {temp()} C, fans {fans()} rpm")
    elif cmd == "set": set_rpm(int(a[1]))
    elif cmd == "max": set_max(not a[1:] == ["off"])
    elif cmd == "auto": release()
    elif cmd == "run": run()
    else: sys.exit(__doc__)

if __name__ == "__main__":
    main(sys.argv[1:])
