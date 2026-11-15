// VictusFan - minimal HP Victus/Omen fan control via the BIOS WMI interface. MIT license.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using Microsoft.Win32;

[assembly: AssemblyTitle("VictusFan")]
[assembly: AssemblyProduct("VictusFan")]
[assembly: AssemblyCompany("lkaixian")]
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

static class App {
    public const string Version = "0.2.1";
    public const string RepoUrl = "https://github.com/lkaixian/omenfan-without-omen";
}

static class Bios {
    const uint CMD = 0x20008;
    public const uint FanCount = 0x10, TempGet = 0x23, FanMax = 0x27, FanGet = 0x2D, FanSet = 0x2E;

    // WMI objects are slow to build, so cache them (and serialise calls: used from worker + UI/CLI threads).
    static readonly object gate = new object();
    static ManagementScope scope; static ManagementObject inst; static ManagementClass inCls;

    public static byte[] Call(uint type, params byte[] data) {
        lock (gate) {
            try {
                if (inst == null) {
                    scope = new ManagementScope(@"root\wmi");
                    var cls = new ManagementClass(scope, new ManagementPath("hpqBIntM"), null);
                    foreach (ManagementObject o in cls.GetInstances()) { inst = o; break; }
                    if (inst == null) throw new Exception("hpqBIntM not found (run as admin?)");
                    inCls = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null);
                }
                var inp = inCls.CreateInstance();
                inp["Sign"] = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
                inp["Command"] = CMD; inp["CommandType"] = type;
                inp["Size"] = (uint)data.Length; inp["hpqBData"] = data; // hpqBData has WmiSizeIs=Size
                var args = inst.GetMethodParameters("hpqBIOSInt128"); args["InData"] = inp;
                var res = inst.InvokeMethod("hpqBIOSInt128", args, null);
                var o2 = (ManagementBaseObject)res["OutData"];
                if ((uint)o2["rwReturnCode"] != 0) throw new Exception("BIOS code " + o2["rwReturnCode"] + " for cmd 0x" + type.ToString("X"));
                return (byte[])o2["Data"];
            } catch (ManagementException) { inst = null; throw; } // rebuild the cache next time
        }
    }

    // BIOS temp command returned 0 on a Victus 16-e0xxx (F.26); fall back to the ACPI thermal zone.
    public static int Temp() {
        int t = 0;
        try { t = Call(TempGet, 0, 0, 0, 0)[0]; } catch { }
        return (t > 0 && t < 120) ? t : ThermalZone();
    }
    public static int ThermalZone() {
        int best = 0;
        try {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                foreach (ManagementObject o in s.Get()) best = Math.Max(best, (int)((uint)o["CurrentTemperature"] / 10 - 273));
        } catch { }
        return best;
    }
    public static int[] Fans() { var d = Call(FanGet, 0, 0, 0, 0); return new[] { d[0] * 100, d[1] * 100 }; }
    public static int FanCountGet() { try { return Call(FanCount, 0, 0, 0, 0)[0]; } catch { return 0; } }
    public static void SetRpm(int rpm) { byte b = (byte)Math.Max(0, Math.Min(65, rpm / 100)); Call(FanSet, b, b); } // clamp 6500 rpm
    public static void Max(bool on) { Call(FanMax, (byte)(on ? 1 : 0)); }
    // Give control back to the BIOS now: otherwise it holds the last manual level until its ~2 min timer expires.
    // ponytail: level 0 = "BIOS auto" is from the Linux hp-wmi driver, unverified on F.26; check fans actually drop.
    public static void Release() { Max(false); SetRpm(0); }

    public static string Wmi(string cls, string prop) {
        try { using (var s = new ManagementObjectSearcher("SELECT " + prop + " FROM " + cls)) foreach (ManagementObject o in s.Get()) return ("" + o[prop]).Trim(); } catch { }
        return "?";
    }
}

class Curve {
    public List<int[]> Pts = new List<int[]>();
    public bool Valid;
    public Curve(string path) {
        try {
            foreach (var l in File.ReadAllLines(path)) {
                var s = l.Split('#')[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (s.Length >= 2) Pts.Add(new[] { int.Parse(s[0]), int.Parse(s[1]) });
            }
        } catch { Pts.Clear(); }
        Pts.Sort((a, b) => a[0].CompareTo(b[0]));
        Valid = Pts.Count >= 2;
        if (!Valid) Reset();
    }
    public void Reset() {
        Pts.Clear();
        Pts.Add(new[] { 40, 2200 }); Pts.Add(new[] { 60, 3200 }); Pts.Add(new[] { 75, 4500 }); Pts.Add(new[] { 85, 5800 });
    }
    public void Save(string path) {
        var l = new List<string> { "# temp_C  fan_rpm   (linear interpolation; the GUI edits this file)" };
        foreach (var p in Pts) l.Add(p[0] + " " + p[1]);
        try { File.WriteAllLines(path, l.ToArray()); } catch { }
    }
    public int Rpm(int t) {
        if (t <= Pts[0][0]) return Pts[0][1];
        for (int i = 1; i < Pts.Count; i++)
            if (t <= Pts[i][0]) { var a = Pts[i - 1]; var b = Pts[i]; return a[1] + (b[1] - a[1]) * (t - a[0]) / (b[0] - a[0]); }
        return Pts[Pts.Count - 1][1];
    }
}

static class Th {
    public static Color Bg, Panel, Fg, Sub, Accent, Grid, Ok, Warn, Bad;
    public static void Set(bool dark) {
        Accent = dark ? Color.FromArgb(0x00, 0xB7, 0xC3) : Color.FromArgb(0x00, 0x7A, 0x87);
        Ok = Color.FromArgb(0x3F, 0xB9, 0x50); Warn = Color.FromArgb(0xD2, 0x99, 0x22); Bad = Color.FromArgb(0xF8, 0x51, 0x49);
        if (dark) { Bg = Color.FromArgb(0x14, 0x17, 0x1C); Panel = Color.FromArgb(0x1F, 0x24, 0x2B); Fg = Color.FromArgb(0xE6, 0xE8, 0xEB); Sub = Color.FromArgb(0x8B, 0x94, 0x9E); Grid = Color.FromArgb(0x33, 0x3A, 0x44); }
        else { Bg = Color.FromArgb(0xF3, 0xF4, 0xF6); Panel = Color.White; Fg = Color.FromArgb(0x1B, 0x1F, 0x24); Sub = Color.FromArgb(0x5B, 0x64, 0x70); Grid = Color.FromArgb(0xD8, 0xDC, 0xE1); }
    }
    public static GraphicsPath Round(RectangleF r, float rad) {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    public static void TitleBar(Form f) { if (f.IsHandleCreated) { int v = Program.Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref v, 4); } }

    // Dark/light colours for menus and the tray menu.
    class Colors : ProfessionalColorTable {
        public override Color ToolStripDropDownBackground { get { return Panel; } }
        public override Color MenuBorder { get { return Grid; } }
        public override Color MenuItemBorder { get { return Grid; } }
        public override Color MenuItemSelected { get { return Grid; } }
        public override Color MenuItemSelectedGradientBegin { get { return Grid; } }
        public override Color MenuItemSelectedGradientEnd { get { return Grid; } }
        public override Color MenuItemPressedGradientBegin { get { return Grid; } }
        public override Color MenuItemPressedGradientMiddle { get { return Grid; } }
        public override Color MenuItemPressedGradientEnd { get { return Grid; } }
        public override Color ImageMarginGradientBegin { get { return Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Panel; } }
        public override Color ImageMarginGradientEnd { get { return Panel; } }
        public override Color MenuStripGradientBegin { get { return Bg; } }
        public override Color MenuStripGradientEnd { get { return Bg; } }
        public override Color SeparatorDark { get { return Grid; } }
        public override Color SeparatorLight { get { return Panel; } }
        public override Color CheckBackground { get { return Accent; } }
        public override Color CheckSelectedBackground { get { return Accent; } }
        public override Color CheckPressedBackground { get { return Accent; } }
    }
    public static void Menu(ToolStrip s) {
        s.Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = false };
        s.BackColor = s is MenuStrip ? Bg : Panel; s.ForeColor = Fg; Items(s.Items);
    }
    static void Items(ToolStripItemCollection items) {
        foreach (ToolStripItem i in items) {
            i.ForeColor = i.Enabled ? Fg : Sub; i.BackColor = Panel;
            var m = i as ToolStripDropDownItem;
            if (m != null) { m.DropDown.Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = false }; m.DropDown.BackColor = Panel; Items(m.DropDownItems); }
        }
    }
}

// Stat tile: big value, caption, optional fill bar.
class Card : Control {
    public string Caption = "", Value = "--"; public Color? ValueColor; public float Frac = -1;
    public Card() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Th.Bg);
        using (var p = Th.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8)) using (var br = new SolidBrush(Th.Panel)) g.FillPath(br, p);
        using (var f = new Font("Segoe UI Semibold", 20f)) using (var br = new SolidBrush(ValueColor ?? Th.Fg)) g.DrawString(Value, f, br, 14, 8);
        using (var f = new Font("Segoe UI", 9f)) using (var br = new SolidBrush(Th.Sub)) g.DrawString(Caption, f, br, 15, 50);
        if (Frac >= 0) {
            float w = Width - 30;
            using (var br = new SolidBrush(Th.Grid)) g.FillRectangle(br, 15, Height - 14, w, 4);
            using (var br = new SolidBrush(Th.Accent)) g.FillRectangle(br, 15, Height - 14, w * Math.Min(1f, Frac), 4);
        }
    }
}

// Segmented mode selector: the selected segment is filled and ticked.
class Seg : Control {
    public string[] Items; public int Sel; public event Action<int> Changed; int hover = -1;
    public Seg(params string[] items) { Items = items; DoubleBuffered = true; Cursor = Cursors.Hand; Height = 40; }
    int At(int x) { return Math.Max(0, Math.Min(Items.Length - 1, x * Items.Length / Math.Max(1, Width))); }
    protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.X); if (h != hover) { hover = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { int i = At(e.X); if (i != Sel) { Sel = i; Invalidate(); if (Changed != null) Changed(i); } }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Th.Bg);
        float w = (Width - 1f) / Items.Length;
        using (var p = Th.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8)) using (var br = new SolidBrush(Th.Panel)) using (var pen = new Pen(Th.Grid)) { g.FillPath(br, p); g.DrawPath(pen, p); }
        using (var f = new Font("Segoe UI Semibold", 10f)) {
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (int i = 0; i < Items.Length; i++) {
                var r = new RectangleF(i * w + 3, 3, w - 5, Height - 7);
                bool on = i == Sel;
                if (on) using (var p = Th.Round(r, 6)) using (var br = new SolidBrush(Th.Accent)) g.FillPath(br, p);
                else if (i == hover) using (var p = Th.Round(r, 6)) using (var br = new SolidBrush(Th.Grid)) g.FillPath(br, p);
                using (var br = new SolidBrush(on ? Color.White : Th.Fg)) g.DrawString((on ? "✓  " : "") + Items[i], f, br, r, sf);
            }
        }
    }
}

class CurveBox : Control {
    const int T0 = 30, T1 = 100, R1 = 6500, RMIN = 1500;
    Curve c; int drag = -1;
    public CurveBox(Curve c) { this.c = c; DoubleBuffered = true; ResizeRedraw = true; }

    Rectangle Plot() { return new Rectangle(50, 14, Width - 50 - 18, Height - 14 - 30); }
    float X(int t) { var p = Plot(); return p.Left + (t - T0) * p.Width / (float)(T1 - T0); }
    float Y(int r) { var p = Plot(); return p.Bottom - r * p.Height / (float)R1; }
    int T(float x) { var p = Plot(); return (int)Math.Round(T0 + (x - p.Left) * (T1 - T0) / p.Width); }
    int R(float y) { var p = Plot(); return (int)Math.Round((p.Bottom - y) * R1 / p.Height / 100.0) * 100; }
    static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
    int Hit(Point m) {
        for (int i = 0; i < c.Pts.Count; i++) {
            float dx = X(c.Pts[i][0]) - m.X, dy = Y(c.Pts[i][1]) - m.Y;
            if (dx * dx + dy * dy <= 100) return i;
        }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Th.Bg);
        using (var bp = Th.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8)) using (var br = new SolidBrush(Th.Panel)) g.FillPath(br, bp);
        var p = Plot();
        using (var grid = new Pen(Th.Grid)) using (var sub = new SolidBrush(Th.Sub)) using (var f = new Font("Segoe UI", 8f)) {
            for (int t = T0; t <= T1; t += 10) { g.DrawLine(grid, X(t), p.Top, X(t), p.Bottom); g.DrawString(t + "°", f, sub, X(t) - 9, p.Bottom + 5); }
            for (int r = 0; r <= 6000; r += 1000) { g.DrawLine(grid, p.Left, Y(r), p.Right, Y(r)); g.DrawString(r.ToString(), f, sub, 10, Y(r) - 8); }
        }
        var pts = new List<PointF>();
        for (int t = T0; t <= T1; t++) pts.Add(new PointF(X(t), Y(c.Rpm(t))));
        using (var pen = new Pen(Th.Accent, 2.5f)) g.DrawLines(pen, pts.ToArray());
        if (Program.Temp > 0) {
            int tt = Clamp(Program.Temp, T0, T1);
            using (var pen = new Pen(Th.Sub) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, X(tt), p.Top, X(tt), p.Bottom);
            using (var br = new SolidBrush(Th.Fg)) g.FillEllipse(br, X(tt) - 4, Y(c.Rpm(tt)) - 4, 8, 8);
        }
        using (var br = new SolidBrush(Th.Accent)) using (var ring = new Pen(Th.Panel, 2f)) using (var f = new Font("Segoe UI", 8.5f, FontStyle.Bold)) using (var fg = new SolidBrush(Th.Fg))
            for (int i = 0; i < c.Pts.Count; i++) {
                float x = X(c.Pts[i][0]), y = Y(c.Pts[i][1]);
                g.FillEllipse(br, x - 6, y - 6, 12, 12); g.DrawEllipse(ring, x - 6, y - 6, 12, 12);
                if (i == drag) g.DrawString(c.Pts[i][0] + "°  " + c.Pts[i][1] + " rpm", f, fg, x + 10, y - 22);
            }
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        int i = Hit(e.Location);
        if (e.Button == MouseButtons.Right) { if (i >= 0 && c.Pts.Count > 2) { c.Pts.RemoveAt(i); Save(); } return; }
        if (i < 0) { // add a point
            int t = Clamp(T(e.X), T0, T1), r = Clamp(R(e.Y), RMIN, R1);
            foreach (var q in c.Pts) if (q[0] == t) return;
            c.Pts.Add(new[] { t, r }); c.Pts.Sort((a, b) => a[0].CompareTo(b[0]));
            i = c.Pts.FindIndex(q => q[0] == t);
        }
        drag = i; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        if (drag < 0) { Cursor = Hit(e.Location) >= 0 ? Cursors.Hand : Cursors.Default; return; }
        int lo = drag > 0 ? c.Pts[drag - 1][0] + 1 : T0, hi = drag < c.Pts.Count - 1 ? c.Pts[drag + 1][0] - 1 : T1;
        c.Pts[drag][0] = Clamp(T(e.X), lo, hi); c.Pts[drag][1] = Clamp(R(e.Y), RMIN, R1);
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { if (drag >= 0) { drag = -1; Save(); } }
    void Save() { c.Save(Program.CurvePath); Invalidate(); }
}

class AboutForm : Form {
    public AboutForm() {
        Text = "About VictusFan"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(400, 250); Font = new Font("Segoe UI", 9f);
        BackColor = Th.Bg; ForeColor = Th.Fg;
        Controls.Add(new PictureBox { Image = FanIcon.Draw(64), Size = new Size(64, 64), Location = new Point(24, 24) });
        Controls.Add(new Label { Text = "VictusFan", Font = new Font("Segoe UI Semibold", 16f), Location = new Point(104, 22), AutoSize = true });
        Controls.Add(new Label { Text = "Version " + App.Version, ForeColor = Th.Sub, Location = new Point(106, 56), AutoSize = true });
        Controls.Add(new Label { Text = "Minimal fan control for HP Victus / Omen laptops.\nTalks to the BIOS over WMI. No driver, no HP software.\n\nMIT License. Use at your own risk: you are overriding\nthe laptop's own thermal management.", Location = new Point(24, 108), Size = new Size(360, 90) });
        if (App.RepoUrl.Length > 0) {
            var l = new LinkLabel { Text = App.RepoUrl, Location = new Point(24, 202), AutoSize = true, LinkColor = Th.Accent };
            l.Click += (s, e) => Process.Start(App.RepoUrl); Controls.Add(l);
        }
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(300, 204), Size = new Size(80, 30), FlatStyle = FlatStyle.Flat, BackColor = Th.Panel, ForeColor = Th.Fg };
        ok.FlatAppearance.BorderColor = Th.Grid; Controls.Add(ok); AcceptButton = ok;
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Th.TitleBar(this); }
}

class MainForm : Form {
    Label lblModel, lblInfo, lblDesc, lblStatus, lblHint;
    Card cTemp, cF1, cF2; Seg seg; CurveBox box; Panel content;
    MenuStrip menu; NotifyIcon tray; ContextMenuStrip trayMenu;
    ToolStripMenuItem miDark, miStart, trayAuto, trayCurve, trayMax, trayHdr, trayStart;
    List<ToolStripMenuItem> miInt = new List<ToolStripMenuItem>();
    System.Windows.Forms.Timer timer;
    static readonly string[] Modes = { "auto", "curve", "max" };

    public MainForm() {
        Text = "VictusFan " + App.Version; Icon = FanIcon.ToIcon(32);
        ClientSize = new Size(696, 584); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 9f);

        content = new Panel { Dock = DockStyle.Fill };
        lblModel = new Label { Font = new Font("Segoe UI Semibold", 14f), Location = new Point(22, 12), AutoSize = true };
        lblInfo = new Label { Location = new Point(24, 42), AutoSize = true };
        cTemp = new Card { Location = new Point(24, 76), Size = new Size(208, 82), Caption = "Temperature (ACPI zone)" };
        cF1 = new Card { Location = new Point(244, 76), Size = new Size(208, 82), Caption = "Fan 1" };
        cF2 = new Card { Location = new Point(464, 76), Size = new Size(208, 82), Caption = "Fan 2" };
        seg = new Seg("Auto (BIOS)", "Fan curve", "Max fan") { Location = new Point(24, 176), Size = new Size(648, 40) };
        seg.Changed += i => SetMode(Modes[i]);
        lblDesc = new Label { Location = new Point(26, 222), Size = new Size(646, 20) };
        box = new CurveBox(Program.Curve) { Location = new Point(24, 248), Size = new Size(648, 252) };
        lblHint = new Label { Text = "Drag points  ·  click empty space to add  ·  right-click a point to remove", Location = new Point(26, 504), AutoSize = true };
        lblStatus = new Label { Location = new Point(24, 536), Size = new Size(648, 20) };
        content.Controls.AddRange(new Control[] { lblModel, lblInfo, cTemp, cF1, cF2, seg, lblDesc, box, lblHint, lblStatus });

        BuildMenu(); BuildTray();
        Controls.Add(content); Controls.Add(menu); MainMenuStrip = menu; // menu last: docks above the fill panel

        Detect(); SetMode(Program.Mode, false); ApplyTheme();
        timer = new System.Windows.Forms.Timer { Interval = 1000 }; // UI refresh only; BIOS polling is on the worker thread
        timer.Tick += (s, e) => Tick(); timer.Start(); Tick();
    }

    static ToolStripMenuItem MI(string text, EventHandler h, Keys k) {
        var m = new ToolStripMenuItem(text); if (h != null) m.Click += h; m.ShortcutKeys = k; return m;
    }

    void BuildMenu() {
        menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(MI("&Import fan curve...", (s, e) => Import(), Keys.Control | Keys.I));
        file.DropDownItems.Add(MI("&Export fan curve...", (s, e) => Export(), Keys.Control | Keys.E));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MI("&Hide to tray", (s, e) => Hide(), Keys.None));
        file.DropDownItems.Add(MI("E&xit", (s, e) => Quit(), Keys.Control | Keys.Q));

        var view = new ToolStripMenuItem("&View");
        miDark = MI("&Dark mode", (s, e) => { Program.Dark = !Program.Dark; Program.SaveCfg(); ApplyTheme(); }, Keys.Control | Keys.D);
        view.DropDownItems.Add(miDark);

        var set = new ToolStripMenuItem("&Settings");
        miStart = MI("Start with &Windows", (s, e) => ToggleStartup(), Keys.None);
        set.DropDownItems.Add(miStart);
        var iv = new ToolStripMenuItem("&Update interval");
        foreach (int sec in new[] { 1, 2, 5 }) {
            int ms = sec * 1000; var m = new ToolStripMenuItem(sec + " second" + (sec > 1 ? "s" : "")) { Tag = ms };
            m.Click += (s, e) => { Program.Interval = ms; Program.SaveCfg(); CheckInterval(); };
            miInt.Add(m); iv.DropDownItems.Add(m);
        }
        set.DropDownItems.Add(iv);
        set.DropDownItems.Add(new ToolStripSeparator());
        set.DropDownItems.Add(MI("Re-&detect hardware", (s, e) => { Detect(); Tick(); }, Keys.F5));
        set.DropDownItems.Add(MI("&Reset fan curve", (s, e) => { Program.Curve.Reset(); Program.Curve.Save(Program.CurvePath); box.Invalidate(); }, Keys.None));

        var help = new ToolStripMenuItem("&Help");
        var gh = MI("&GitHub", (s, e) => Process.Start(App.RepoUrl), Keys.None); gh.Enabled = App.RepoUrl.Length > 0;
        help.DropDownItems.Add(gh);
        help.DropDownItems.Add(MI("Open app &folder", (s, e) => Process.Start("explorer.exe", Program.Dir), Keys.None));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(MI("&About VictusFan", (s, e) => { using (var a = new AboutForm()) a.ShowDialog(this); }, Keys.F1));
        menu.Items.AddRange(new ToolStripItem[] { file, view, set, help });
    }

    void BuildTray() {
        trayMenu = new ContextMenuStrip();
        trayHdr = new ToolStripMenuItem("VictusFan") { Enabled = false, Font = new Font("Segoe UI Semibold", 9f) };
        trayAuto = MI("Auto (BIOS)", (s, e) => SetMode("auto"), Keys.None);
        trayCurve = MI("Fan curve", (s, e) => SetMode("curve"), Keys.None);
        trayMax = MI("Max fan", (s, e) => SetMode("max"), Keys.None);
        trayStart = MI("Start with Windows", (s, e) => ToggleStartup(), Keys.None);
        trayMenu.Items.AddRange(new ToolStripItem[] { trayHdr, new ToolStripSeparator(), trayAuto, trayCurve, trayMax, new ToolStripSeparator(),
            trayStart, new ToolStripSeparator(), MI("Open VictusFan", (s, e) => ShowMe(), Keys.None), MI("Exit", (s, e) => Quit(), Keys.None) });
        trayMenu.Opening += (s, e) => trayStart.Checked = Program.StartupOn(); // Task Manager can change it behind our back
        tray = new NotifyIcon { Icon = Icon, Visible = true, Text = "VictusFan", ContextMenuStrip = trayMenu };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMe(); };
    }

    void ToggleStartup() {
        string err = Program.StartupSet(!Program.StartupOn());
        if (err != null) MessageBox.Show("Could not change startup setting:\n" + err, "VictusFan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        miStart.Checked = trayStart.Checked = Program.StartupOn();
    }

    void Quit() { Program.Stop = true; tray.Visible = false; Program.Safe(Bios.Release); Application.Exit(); }
    public void ShowMe() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } base.OnFormClosing(e); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Th.TitleBar(this); }

    void Detect() { // WMI queries are slow: run off the UI thread
        ThreadPool.QueueUserWorkItem(o => {
            string model = Bios.Wmi("Win32_ComputerSystem", "Model");
            string info = "BIOS " + Bios.Wmi("Win32_BIOS", "SMBIOSBIOSVersion") + "   ·   " + Bios.FanCountGet() + " fans   ·   " + Bios.Wmi("Win32_Processor", "Name");
            try { BeginInvoke(new Action(() => { lblModel.Text = model; lblInfo.Text = info; })); } catch { }
        });
    }

    void Import() {
        using (var d = new OpenFileDialog { Filter = "Fan curve (*.txt)|*.txt|All files|*.*", Title = "Import fan curve" }) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var c = new Curve(d.FileName);
            if (!c.Valid) { MessageBox.Show(this, "That file has no valid curve (need at least 2 lines of \"temp rpm\").", "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Program.Curve.Pts.Clear(); Program.Curve.Pts.AddRange(c.Pts); Program.Curve.Save(Program.CurvePath); box.Invalidate();
        }
    }
    void Export() {
        using (var d = new SaveFileDialog { Filter = "Fan curve (*.txt)|*.txt", FileName = "victusfan-curve.txt", Title = "Export fan curve" })
            if (d.ShowDialog(this) == DialogResult.OK) Program.Curve.Save(d.FileName);
    }

    void CheckInterval() { foreach (var m in miInt) m.Checked = (int)m.Tag == Program.Interval; }

    void SetMode(string m) { SetMode(m, true); }
    void SetMode(string m, bool apply) {
        Program.Mode = m; Program.SaveCfg();
        seg.Sel = Array.IndexOf(Modes, m); seg.Invalidate();
        trayAuto.Checked = m == "auto"; trayCurve.Checked = m == "curve"; trayMax.Checked = m == "max";
        lblDesc.Text = m == "auto" ? "The BIOS controls the fans. VictusFan does not override anything."
            : m == "curve" ? "Fan speed follows the curve below and is refreshed every few seconds."
            : "Fans run at full speed.";
        if (apply) Program.Dirty = true; // worker applies it
    }

    void ApplyTheme() {
        Th.Set(Program.Dark); BackColor = Th.Bg; ForeColor = Th.Fg; content.BackColor = Th.Bg;
        foreach (Control c in content.Controls) if (c is Label) c.ForeColor = Th.Sub;
        lblModel.ForeColor = Th.Fg; lblDesc.ForeColor = Th.Fg;
        Th.Menu(menu); Th.Menu(trayMenu);
        miDark.Checked = Program.Dark; miStart.Checked = trayStart.Checked = Program.StartupOn(); CheckInterval();
        Th.TitleBar(this);
        foreach (Control c in content.Controls) c.Invalidate();
    }

    void Tick() {
        string err = Program.Err;
        cTemp.Value = Program.Temp > 0 ? Program.Temp + " °C" : "n/a";
        cTemp.ValueColor = Program.Temp >= 78 ? Th.Bad : Program.Temp >= 65 ? Th.Warn : Th.Ok;
        cF1.Value = Program.F1 + " rpm"; cF1.Frac = Program.F1 / 6500f;
        cF2.Value = Program.F2 + " rpm"; cF2.Frac = Program.F2 / 6500f;
        lblStatus.ForeColor = err == null ? Th.Ok : Th.Bad;
        lblStatus.Text = err == null ? "● Connected to BIOS  ·  mode: " + Program.Mode + "  ·  updating every " + Program.Interval / 1000 + " s" : "● " + err;
        trayHdr.Text = Program.Temp + " °C  ·  " + Program.F1 + " / " + Program.F2 + " rpm";
        var tip = Program.Mode + " " + Program.Temp + "°C " + Program.F1 + "/" + Program.F2;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        cTemp.Invalidate(); cF1.Invalidate(); cF2.Invalidate(); box.Invalidate();
    }
}

static class Program {
    public static string Dir = AppDomain.CurrentDomain.BaseDirectory;
    public static string CurvePath = Path.Combine(Dir, "curve.txt"), CfgPath = Path.Combine(Dir, "settings.txt");
    public static Curve Curve;
    public static string Mode = "curve"; // curve | max | auto
    public static bool Dark;
    public static int Interval = 2000;
    public static int Temp, F1, F2;
    static List<int> hist = new List<int>();

    [STAThread] static int Main(string[] a) {
        if (a.Length > 0 && a[0] != "--tray") return Cli(a);
        bool first;
        using (var m = new Mutex(true, "VictusFan.single", out first)) {
            if (!first) return 0;
            Curve = new Curve(CurvePath); LoadCfg();
            Application.EnableVisualStyles();
            Th.Set(Dark);
            var f = new MainForm();
            if (a.Length == 0) f.ShowMe();
            StartWorker();
            Application.Run(new ApplicationContext());
        }
        return 0;
    }

    public static void LoadCfg() {
        try {
            Dark = (int)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
        } catch { Dark = true; }
        try {
            foreach (var l in File.ReadAllLines(CfgPath)) {
                var kv = l.Split('=');
                if (kv.Length != 2) continue;
                if (kv[0] == "mode") Mode = kv[1];
                if (kv[0] == "dark") Dark = kv[1] == "1";
                if (kv[0] == "interval") { int v; if (int.TryParse(kv[1], out v) && v >= 1000 && v <= 10000) Interval = v; }
            }
        } catch { }
    }
    public static void SaveCfg() { try { File.WriteAllLines(CfgPath, new[] { "mode=" + Mode, "dark=" + (Dark ? "1" : "0"), "interval=" + Interval }); } catch { } }

    // The ACPI zone spikes (e.g. 59 -> 70 -> 59), so use the median of the last 5 readings.
    static int Median(int raw) {
        if (raw > 0) { hist.Add(raw); if (hist.Count > 5) hist.RemoveAt(0); }
        if (hist.Count == 0) return 0;
        var s = new List<int>(hist); s.Sort(); return s[s.Count / 2];
    }

    // BIOS work runs on its own thread so slow WMI calls never freeze the window or tray.
    public static volatile string Err;
    public static volatile bool Dirty = true, Stop;
    public static void StartWorker() {
        var t = new Thread(() => {
            while (!Stop) {
                Err = Step();
                for (int i = 0; i < Interval / 100 && !Stop && !Dirty; i++) Thread.Sleep(100); // wake early on a mode change
            }
        }) { IsBackground = true };
        t.Start();
    }

    // Returns an error message, or null when OK.
    static string Step() {
        try {
            bool changed = Dirty; Dirty = false;
            Temp = Median(Bios.Temp());
            if (Mode == "curve") { if (changed) Bios.Max(false); Bios.SetRpm(Curve.Rpm(Temp)); }
            else if (Mode == "max") Bios.Max(true);
            else if (changed) Bios.Release(); // auto: hand control back once
            var f = Bios.Fans(); F1 = f[0]; F2 = f[1];
            return null;
        } catch (Exception e) { return e.Message; }
    }
    public static void Safe(Action x) { try { x(); } catch { } }

    static int Sch(string args) {
        var p = Process.Start(new ProcessStartInfo("schtasks", args) { CreateNoWindow = true, UseShellExecute = false });
        p.WaitForExit(); return p.ExitCode;
    }
    // Startup = a Run-key entry (so it shows in Task Manager > Startup apps and can be toggled there) that fires an
    // on-demand elevated task. Windows won't autostart an admin-only exe straight from Run, hence the task.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    static readonly string SchExe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static bool StartupOn() {
        try {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) if (k == null || k.GetValue("VictusFan") == null) return false;
            using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey)) { // Task Manager's Disable flips the low bit of the first byte
                var v = k == null ? null : k.GetValue("VictusFan") as byte[];
                if (v != null && v.Length > 0 && (v[0] & 1) == 1) return false;
            }
            return true;
        } catch { return false; }
    }
    // Returns an error message, or null on success.
    public static string StartupSet(bool on) {
        try {
            if (!on) {
                Sch("/Delete /F /TN VictusFan");
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("VictusFan", false);
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true)) if (k != null) k.DeleteValue("VictusFan", false);
                return null;
            }
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<Principals><Principal id=\"A\"><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
                + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><AllowStartOnDemand>true</AllowStartOnDemand></Settings>"
                + "<Actions Context=\"A\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--tray</Arguments></Exec></Actions></Task>";
            var tmp = Path.Combine(Path.GetTempPath(), "victusfan-task.xml");
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            int rc = Sch("/Create /F /TN VictusFan /XML \"" + tmp + "\""); File.Delete(tmp);
            if (rc != 0) return "schtasks failed (exit code " + rc + ")";
            var launcher = Path.Combine(Dir, "VictusFanStart.exe"); // named "VictusFan" in Task Manager; falls back to schtasks itself
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue("VictusFan", File.Exists(launcher) ? "\"" + launcher + "\"" : "\"" + SchExe + "\" /run /tn VictusFan");
            using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true)) if (k != null) k.DeleteValue("VictusFan", false); // clear any old "disabled" flag
            return null;
        } catch (Exception e) { return e.Message; }
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    static int Cli(string[] a) {
        AttachConsole(-1); // winexe has no console of its own; reuse the parent's
        try {
            switch (a[0]) {
                case "status":
                    var f = Bios.Fans();
                    Console.WriteLine("temp {0}C  fan {1}/{2} rpm  fans {3}", Bios.Temp(), f[0], f[1], Bios.FanCountGet());
                    break;
                case "probe": // find a working temperature source
                    Console.WriteLine("ACPI thermal zone: " + Bios.ThermalZone() + "C");
                    for (byte i = 0; i < 4; i++) {
                        try { var d = Bios.Call(Bios.TempGet, i, 0, 0, 0); Console.WriteLine("0x23 in={0}: {1} {2} {3} {4}", i, d[0], d[1], d[2], d[3]); }
                        catch (Exception e) { Console.WriteLine("0x23 in={0}: {1}", i, e.Message); }
                    }
                    break;
                case "set": Bios.Max(false); Bios.SetRpm(int.Parse(a[1])); Console.WriteLine("set (BIOS reverts in ~2min unless the app keeps running)"); break;
                case "max": Bios.Max(a.Length < 2 || a[1] != "off"); break;
                case "auto": Bios.Release(); Console.WriteLine("released to BIOS"); break;
                default: Console.WriteLine("VictusFan [status | probe | set <rpm> | max [off] | --tray]  (no args = window)"); break;
            }
            return 0;
        } catch (Exception e) { Console.WriteLine(e.ToString()); return 1; }
    }
}
