// Draws the app icon in code, so no image assets are shipped. MIT license.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class FanIcon {
    public static Bitmap Draw(int s) {
        var b = new Bitmap(s, s);
        using (var g = Graphics.FromImage(b)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var accent = Color.FromArgb(0x00, 0xB7, 0xC3);
            using (var br = new SolidBrush(Color.FromArgb(0x1B, 0x20, 0x27))) g.FillEllipse(br, 0, 0, s - 1, s - 1);
            using (var pen = new Pen(accent, s * 0.055f)) g.DrawEllipse(pen, s * 0.05f, s * 0.05f, s * 0.9f, s * 0.9f);
            g.TranslateTransform(s / 2f, s / 2f);
            using (var br = new SolidBrush(Color.White)) {
                for (int i = 0; i < 4; i++) {
                    g.FillEllipse(br, s * 0.05f, -s * 0.2f, s * 0.31f, s * 0.19f); // blade, offset for a swirl
                    g.RotateTransform(90);
                }
            }
            using (var br = new SolidBrush(accent)) g.FillEllipse(br, -s * 0.075f, -s * 0.075f, s * 0.15f, s * 0.15f);
        }
        return b;
    }

    public static Icon ToIcon(int s) { return Icon.FromHandle(Draw(s).GetHicon()); }

    // Single 256px PNG-in-ICO file (Vista+), used by csc /win32icon.
    public static void WriteIco(string path) {
        byte[] png;
        using (var ms = new MemoryStream()) { Draw(256).Save(ms, ImageFormat.Png); png = ms.ToArray(); }
        using (var w = new BinaryWriter(File.Create(path))) {
            w.Write((short)0); w.Write((short)1); w.Write((short)1);       // ICONDIR
            w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); // 256x256, no palette
            w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(22);
            w.Write(png);
        }
    }
}
