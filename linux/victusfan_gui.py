#!/usr/bin/python3
"""VictusFan tray + window (Linux port). Needs victusfan_wmi loaded and /dev/victusfan access (see install.sh)."""
import os, sys, threading, time, gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk, GLib
import victusfan as vf

try:
    gi.require_version("AyatanaAppIndicator3", "0.1"); from gi.repository import AyatanaAppIndicator3 as AI
except (ValueError, ImportError):
    try: gi.require_version("AppIndicator3", "0.1"); from gi.repository import AppIndicator3 as AI
    except (ValueError, ImportError): AI = None  # no tray library: window only, closing quits

HERE = os.path.dirname(os.path.abspath(__file__))
CFG_DIR = os.path.expanduser("~/.config/victusfan")
CFG = CFG_DIR + "/settings.txt"
AUTOSTART = os.path.expanduser("~/.config/autostart/victusfan.desktop")
REPO = "https://github.com/lkaixian/omenfan-without-omen"
T0, T1, R1, RMIN = 30, 100, 6500, 1500

S = {"mode": "curve", "dark": False, "interval": 2, "temp": 0, "f1": 0, "f2": 0, "err": None, "stop": False, "dirty": True}
curve = [list(p) for p in vf.load_curve()]

def load_cfg():
    try:
        for l in open(CFG):
            k, _, v = l.strip().partition("=")
            if k == "mode" and v in ("auto", "curve", "max"): S["mode"] = v
            elif k == "dark": S["dark"] = v == "1"
            elif k == "interval" and v in ("1", "2", "5"): S["interval"] = int(v)
    except OSError: pass

def save_cfg():
    os.makedirs(CFG_DIR, exist_ok=True)
    open(CFG, "w").write(f"mode={S['mode']}\ndark={int(S['dark'])}\ninterval={S['interval']}\n")

def save_curve(): vf.save_curve([tuple(p) for p in curve])
def rpm_at(t): return vf.curve_rpm([tuple(p) for p in curve], t)

def worker():  # BIOS polling off the UI thread; re-applies every tick (BIOS drops manual levels after ~2 min)
    hist, last = [], None
    while not S["stop"]:
        try:
            hist = (hist + [vf.temp()])[-5:]; S["temp"] = sorted(hist)[len(hist) // 2]
            S["f1"], S["f2"] = vf.fans()
            m = S["mode"]
            if m == "curve": vf.set_rpm(rpm_at(S["temp"]))
            elif m == "max": vf.set_max(True)
            elif m != last: vf.release()
            last = m; S["err"] = None
        except (OSError, RuntimeError, IndexError) as e:
            S["err"] = "Cannot reach BIOS: " + str(e)
        for _ in range(S["interval"] * 10):
            if S["stop"] or S["dirty"]: break
            time.sleep(0.1)
        S["dirty"] = False

class CurveBox(Gtk.DrawingArea):
    def __init__(self, on_change):
        super().__init__(); self.drag = -1; self.on_change = on_change; self.set_size_request(560, 260)
        self.add_events(Gdk.EventMask.BUTTON_PRESS_MASK | Gdk.EventMask.BUTTON_RELEASE_MASK | Gdk.EventMask.POINTER_MOTION_MASK)
        self.connect("draw", self.on_draw); self.connect("button-press-event", self.on_down)
        self.connect("motion-notify-event", self.on_move); self.connect("button-release-event", self.on_up)
    def plot(self): w, h = self.get_allocated_width(), self.get_allocated_height(); return 50, 14, w - 68, h - 44
    def X(self, t): l, _, w, _ = self.plot(); return l + (t - T0) * w / (T1 - T0)
    def Y(self, r): _, tp, _, h = self.plot(); return tp + h - r * h / R1
    def T(self, x): l, _, w, _ = self.plot(); return round(T0 + (x - l) * (T1 - T0) / w)
    def R(self, y): _, tp, _, h = self.plot(); return round((tp + h - y) * R1 / h / 100) * 100
    def hit(self, x, y):
        for i, (t, r) in enumerate(curve):
            if (self.X(t) - x) ** 2 + (self.Y(r) - y) ** 2 <= 100: return i
        return -1
    def on_draw(self, w, c):
        d = S["dark"]; fg = (.9, .91, .92) if d else (.1, .12, .14); sub = (.55, .58, .62) if d else (.36, .39, .44)
        grid = (.2, .23, .27) if d else (.85, .86, .88); acc = (0, .72, .76) if d else (0, .48, .53)
        l, tp, pw, ph = self.plot(); c.set_font_size(11)
        c.set_source_rgb(*((.12, .14, .17) if d else (1, 1, 1))); c.paint()
        c.set_line_width(1)
        for t in range(T0, T1 + 1, 10):
            c.set_source_rgb(*grid); c.move_to(self.X(t), tp); c.line_to(self.X(t), tp + ph); c.stroke()
            c.set_source_rgb(*sub); c.move_to(self.X(t) - 10, tp + ph + 18); c.show_text(f"{t}°")
        for r in range(0, 6001, 1000):
            c.set_source_rgb(*grid); c.move_to(l, self.Y(r)); c.line_to(l + pw, self.Y(r)); c.stroke()
            c.set_source_rgb(*sub); c.move_to(8, self.Y(r) + 4); c.show_text(str(r))
        c.set_source_rgb(*acc); c.set_line_width(2.5)
        for t in range(T0, T1 + 1):
            (c.move_to if t == T0 else c.line_to)(self.X(t), self.Y(rpm_at(t)))
        c.stroke()
        if S["temp"] > 0:
            tt = max(T0, min(T1, S["temp"])); c.set_source_rgb(*sub); c.set_line_width(1); c.set_dash([4, 3])
            c.move_to(self.X(tt), tp); c.line_to(self.X(tt), tp + ph); c.stroke(); c.set_dash([])
            c.set_source_rgb(*fg); c.arc(self.X(tt), self.Y(rpm_at(tt)), 4, 0, 6.3); c.fill()
        for i, (t, r) in enumerate(curve):
            c.set_source_rgb(*acc); c.arc(self.X(t), self.Y(r), 6, 0, 6.3); c.fill()
            if i == self.drag:
                c.set_source_rgb(*fg); c.move_to(self.X(t) + 10, self.Y(r) - 12); c.show_text(f"{t}°  {r} rpm")
    def on_down(self, w, e):
        i = self.hit(e.x, e.y)
        if e.button == 3:
            if i >= 0 and len(curve) > 2: del curve[i]; self.changed()
            return
        if i < 0:  # add a point
            t, r = max(T0, min(T1, self.T(e.x))), max(RMIN, min(R1, self.R(e.y)))
            if any(p[0] == t for p in curve): return
            curve.append([t, r]); curve.sort(); i = [p[0] for p in curve].index(t)
        self.drag = i; self.queue_draw()
    def on_move(self, w, e):
        i = self.drag
        if i < 0: return
        lo = curve[i - 1][0] + 1 if i > 0 else T0; hi = curve[i + 1][0] - 1 if i < len(curve) - 1 else T1
        curve[i][0] = max(lo, min(hi, self.T(e.x))); curve[i][1] = max(RMIN, min(R1, self.R(e.y))); self.queue_draw()
    def on_up(self, w, e):
        if self.drag >= 0: self.drag = -1; self.changed()
    def changed(self): save_curve(); S["dirty"] = True; self.queue_draw()

class Win(Gtk.Window):
    def __init__(self, app):
        super().__init__(title="VictusFan"); self.app = app; self.set_default_size(620, 560); self.set_border_width(12)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8); self.add(box)
        bar = Gtk.MenuBar(); box.pack_start(bar, False, False, 0)
        def item(menu, label, cb, check=False):
            m = Gtk.CheckMenuItem(label=label) if check else Gtk.MenuItem(label=label); m.connect("activate", cb); menu.append(m); return m
        def top(label): m = Gtk.MenuItem.new_with_mnemonic(label); sub = Gtk.Menu(); m.set_submenu(sub); bar.append(m); return sub
        f = top("_File")
        item(f, "Import fan curve…", lambda *_: self.imp()); item(f, "Export fan curve…", lambda *_: self.exp())
        f.append(Gtk.SeparatorMenuItem()); item(f, "Hide to tray", lambda *_: self.hide()); item(f, "Exit", lambda *_: app.quit())
        v = top("_View"); self.mi_dark = item(v, "Dark mode", lambda m: app.set_dark(m.get_active()) if not app.busy else None, True)
        s = top("_Settings"); self.mi_start = item(s, "Start at login", lambda m: app.set_autostart(m.get_active()) if not app.busy else None, True)
        iv = Gtk.MenuItem(label="Update interval"); ivm = Gtk.Menu(); iv.set_submenu(ivm); s.append(iv); self.mi_iv = {}
        grp = None
        for sec in (1, 2, 5):
            m = Gtk.RadioMenuItem.new_with_label_from_widget(grp, f"{sec} second" + ("s" if sec > 1 else "")); grp = grp or m
            m.connect("activate", lambda m, sec=sec: app.set_interval(sec) if m.get_active() and not app.busy else None); ivm.append(m); self.mi_iv[sec] = m
        s.append(Gtk.SeparatorMenuItem())
        item(s, "Reset fan curve", lambda *_: self.reset())
        h = top("_Help"); item(h, "GitHub", lambda *_: Gtk.show_uri_on_window(self, REPO, Gdk.CURRENT_TIME)); item(h, "About VictusFan", lambda *_: self.about())

        self.model = Gtk.Label(xalign=0); self.info = Gtk.Label(xalign=0)
        try: self.model.set_markup("<b>" + open("/sys/class/dmi/id/product_name").read().strip() + "</b>")
        except OSError: pass
        box.pack_start(self.model, False, False, 0)
        self.info.set_text(f"BIOS {self.dmi('bios_version')}   ·   {self.cpu()}"); box.pack_start(self.info, False, False, 0)
        cards = Gtk.Box(spacing=8, homogeneous=True); box.pack_start(cards, False, False, 4)
        self.cards = {}
        for k, name in (("temp", "Temperature"), ("f1", "Fan 1 (CPU)"), ("f2", "Fan 2 (GPU)")):
            fr = Gtk.Frame(); b = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, margin=8)
            n = Gtk.Label(label=name, xalign=0); val = Gtk.Label(xalign=0); lv = Gtk.LevelBar(min_value=0, max_value=1)
            b.add(n); b.add(val); b.add(lv); fr.add(b); cards.add(fr); self.cards[k] = (val, lv)
        seg = Gtk.Box(spacing=0, homogeneous=True); box.pack_start(seg, False, False, 4); self.rb = {}; grp = None
        for m, label in (("auto", "Auto (BIOS)"), ("curve", "Fan curve"), ("max", "Max fan")):
            r = Gtk.RadioButton.new_with_label_from_widget(grp, label); r.set_mode(False); grp = grp or r
            r.connect("toggled", lambda r, m=m: app.set_mode(m) if r.get_active() and not app.busy else None); seg.add(r); self.rb[m] = r
        self.desc = Gtk.Label(xalign=0); box.pack_start(self.desc, False, False, 0)
        self.curvebox = CurveBox(lambda: None); box.pack_start(self.curvebox, True, True, 0)
        box.pack_start(Gtk.Label(label="Click to add a point, drag to move, right-click to remove.", xalign=0), False, False, 0)
        self.status = Gtk.Label(xalign=0); box.pack_start(self.status, False, False, 0)
        self.connect("delete-event", lambda *_: self.on_close())
        self.show_all()

    def dmi(self, n):
        try: return open("/sys/class/dmi/id/" + n).read().strip()
        except OSError: return "?"
    def cpu(self):
        try: return next(l.split(":", 1)[1].strip() for l in open("/proc/cpuinfo") if l.startswith("model name"))
        except (OSError, StopIteration): return "?"
    def on_close(self):
        if self.app.ind: self.hide()
        else: self.app.quit()
        return True
    def imp(self):
        d = Gtk.FileChooserDialog(title="Import fan curve", parent=self, action=Gtk.FileChooserAction.OPEN)
        d.add_buttons("Cancel", Gtk.ResponseType.CANCEL, "Open", Gtk.ResponseType.OK)
        if d.run() == Gtk.ResponseType.OK:
            pts = vf.load_curve(d.get_filename())
            if not self.valid(d.get_filename()):
                m = Gtk.MessageDialog(parent=self, message_type=Gtk.MessageType.WARNING, buttons=Gtk.ButtonsType.OK,
                                      text='That file has no valid curve (need at least 2 lines of "temp rpm").'); m.run(); m.destroy()
            else:
                curve[:] = [list(p) for p in pts]; self.curvebox.changed()
        d.destroy()
    def valid(self, path):  # load_curve falls back to DEFAULT on bad files; tell the two apart
        try: return sum(1 for l in open(path) if len(l.split("#")[0].split()) >= 2) >= 2
        except OSError: return False
    def exp(self):
        d = Gtk.FileChooserDialog(title="Export fan curve", parent=self, action=Gtk.FileChooserAction.SAVE)
        d.add_buttons("Cancel", Gtk.ResponseType.CANCEL, "Save", Gtk.ResponseType.OK); d.set_current_name("victusfan-curve.txt"); d.set_do_overwrite_confirmation(True)
        if d.run() == Gtk.ResponseType.OK: vf.save_curve([tuple(p) for p in curve], d.get_filename())
        d.destroy()
    def reset(self): curve[:] = [list(p) for p in vf.DEFAULT]; self.curvebox.changed()
    def about(self):
        d = Gtk.AboutDialog(transient_for=self, program_name="VictusFan", version="0.2.2", website=REPO, license_type=Gtk.License.MIT_X11,
            comments="Minimal fan control for HP Victus / Omen laptops.\nTalks to the BIOS over WMI. Use at your own risk: you are overriding the laptop's own thermal management.")
        d.run(); d.destroy()

class App:
    def __init__(self, hidden):
        load_cfg(); self.busy = False; self.ind = None
        Gtk.Settings.get_default().set_property("gtk-application-prefer-dark-theme", S["dark"])
        self.win = Win(self)
        if AI:
            self.ind = AI.Indicator.new_with_path("victusfan", "victusfan", AI.IndicatorCategory.HARDWARE, HERE)
            self.ind.set_status(AI.IndicatorStatus.ACTIVE); self.build_tray()
        if hidden and self.ind: self.win.hide()
        self.sync(); threading.Thread(target=worker, daemon=True).start(); GLib.timeout_add(1000, self.tick); self.tick()

    def build_tray(self):
        m = Gtk.Menu(); self.t_hdr = Gtk.MenuItem(label="VictusFan"); self.t_hdr.set_sensitive(False); m.append(self.t_hdr); m.append(Gtk.SeparatorMenuItem())
        self.t_rb = {}; grp = None
        for k, label in (("auto", "Auto (BIOS)"), ("curve", "Fan curve"), ("max", "Max fan")):
            r = Gtk.RadioMenuItem.new_with_label_from_widget(grp, label); grp = grp or r
            r.connect("activate", lambda r, k=k: self.set_mode(k) if r.get_active() and not self.busy else None); m.append(r); self.t_rb[k] = r
        m.append(Gtk.SeparatorMenuItem())
        self.t_start = Gtk.CheckMenuItem(label="Start at login"); self.t_start.connect("activate", lambda r: self.set_autostart(r.get_active()) if not self.busy else None); m.append(self.t_start)
        m.append(Gtk.SeparatorMenuItem())
        for label, cb in (("Open VictusFan", lambda *_: self.win.present()), ("Exit", lambda *_: self.quit())):
            i = Gtk.MenuItem(label=label); i.connect("activate", cb); m.append(i)
        m.show_all(); self.ind.set_menu(m)

    def sync(self):  # push state into every widget without re-triggering handlers
        self.busy = True
        self.win.rb[S["mode"]].set_active(True); self.win.mi_dark.set_active(S["dark"]); self.win.mi_iv[S["interval"]].set_active(True)
        on = os.path.exists(AUTOSTART); self.win.mi_start.set_active(on)
        if self.ind: self.t_rb[S["mode"]].set_active(True); self.t_start.set_active(on)
        self.win.desc.set_text({"auto": "The BIOS controls the fans.", "max": "Fans run at full speed.",
            "curve": "Fan speed follows the curve below and is refreshed every few seconds."}[S["mode"]])
        self.busy = False

    def set_mode(self, m): S["mode"] = m; S["dirty"] = True; save_cfg(); self.sync()
    def set_dark(self, d): S["dark"] = d; save_cfg(); Gtk.Settings.get_default().set_property("gtk-application-prefer-dark-theme", d); self.win.curvebox.queue_draw()
    def set_interval(self, s): S["interval"] = s; save_cfg(); S["dirty"] = True
    def set_autostart(self, on):
        try:
            if on:
                os.makedirs(os.path.dirname(AUTOSTART), exist_ok=True)
                open(AUTOSTART, "w").write(f"[Desktop Entry]\nType=Application\nName=VictusFan\nExec=/usr/bin/python3 {os.path.abspath(__file__)} --autostart\nIcon=victusfan\nX-GNOME-Autostart-enabled=true\n")
            elif os.path.exists(AUTOSTART): os.remove(AUTOSTART)
        except OSError as e:
            m = Gtk.MessageDialog(parent=self.win, message_type=Gtk.MessageType.WARNING, buttons=Gtk.ButtonsType.OK, text=f"Could not change startup setting:\n{e}"); m.run(); m.destroy()
        self.sync()

    def tick(self):
        t, f1, f2, err = S["temp"], S["f1"], S["f2"], S["err"]; w = self.win
        w.cards["temp"][0].set_markup(f'<span size="x-large" foreground="{"#F85149" if t >= 78 else "#D29922" if t >= 65 else "#3FB950"}">{t} °C</span>' if t > 0 else "n/a")
        w.cards["temp"][1].set_value(min(1, t / 100))
        for k, v in (("f1", f1), ("f2", f2)): w.cards[k][0].set_markup(f'<span size="x-large">{v} rpm</span>'); w.cards[k][1].set_value(min(1, v / R1))
        w.status.set_markup(f'<span foreground="#3FB950">● Connected to BIOS  ·  mode: {S["mode"]}  ·  updating every {S["interval"]} s</span>' if not err
                            else f'<span foreground="#F85149">● {GLib.markup_escape_text(err)}</span>')
        if self.ind:
            self.t_hdr.set_label(f"{t} °C  ·  {f1} / {f2} rpm"); self.ind.set_title(f"{S['mode']} {t}°C {f1}/{f2}")
            self.t_start.set_active(os.path.exists(AUTOSTART)) if not self.busy else None
        w.curvebox.queue_draw(); return True

    def quit(self):
        S["stop"] = True; time.sleep(0.15)
        try: vf.release()
        except (OSError, RuntimeError): pass
        Gtk.main_quit()

if __name__ == "__main__":
    if not os.path.exists(vf.DEV) or not os.access(vf.DEV, os.R_OK | os.W_OK):
        print("No access to", vf.DEV, "- load the module / run install.sh", file=sys.stderr)
    GLib.set_prgname("victusfan"); GLib.set_application_name("VictusFan")  # Wayland app_id must match victusfan.desktop
    Gtk.Window.set_default_icon_name("victusfan")
    app = App("--autostart" in sys.argv)
    import signal; signal.signal(signal.SIGINT, signal.SIG_DFL); signal.signal(signal.SIGTERM, lambda *_: app.quit())
    Gtk.main()
