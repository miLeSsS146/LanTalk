// What the person here sees while another PC uses the mouse and keyboard: a frame around every screen and a ring where it clicks.
// All of it lets clicks through and isn't in screenshots.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LanTalk {

// A small window in front of everything, placed in real pixels.
public class GhostForm : Form {
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    protected static readonly Color Hole = Color.FromArgb(1, 2, 3); // not drawn
    public GhostForm() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None; Enabled = false; }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 8 | 0x20 | 0x80000; return p; } } // no activation, not in Alt+Tab, topmost, click-through, layered
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetLayeredWindowAttributes(Handle, (uint)ColorTranslator.ToWin32(Hole), 255, 3); SetWindowDisplayAffinity(Handle, 0x11); }
    public void Put(Rectangle r) {
        Shot.RealPixels(delegate {
            IntPtr h = Handle;
            SetWindowPos(h, (IntPtr)(-1), r.X, r.Y, r.Width, r.Height, 0x10 | 0x40); // no activation, show
            return 0;
        });
    }
    public void Away() { if (IsHandleCreated) SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | 0x80); }
}

public class ScreenEdge : IDisposable {
    readonly List<GhostForm> sides = new List<GhostForm>();
    string shown = "";
    public void Show(bool on) {
        List<Rectangle> m = on ? Shot.Monitors() : new List<Rectangle>();
        string now = string.Join(";", m.Select(delegate (Rectangle r) { return r.ToString(); }).ToArray());
        if (now == shown) return;
        shown = now;
        while (sides.Count < m.Count * 4) { GhostForm f = new GhostForm(); f.BackColor = Theme.Accent; sides.Add(f); }
        int t = Theme.S(4);
        for (int i = 0; i < sides.Count; i++) {
            if (i / 4 >= m.Count) { sides[i].Away(); continue; }
            Rectangle r = m[i / 4];
            sides[i].Put(i % 4 == 0 ? new Rectangle(r.X, r.Y, r.Width, t) : i % 4 == 1 ? new Rectangle(r.X, r.Bottom - t, r.Width, t) : i % 4 == 2 ? new Rectangle(r.X, r.Y + t, t, r.Height - 2 * t) : new Rectangle(r.Right - t, r.Y + t, t, r.Height - 2 * t));
        }
    }
    public void Dispose() { foreach (GhostForm f in sides) f.Dispose(); sides.Clear(); shown = ""; }
}

public class ClickRing : GhostForm {
    readonly Timer off = new Timer();
    public ClickRing() { BackColor = Hole; off.Interval = 700; off.Tick += delegate { off.Stop(); Away(); }; }
    public void At(Point p) { int d = Theme.S(56); Put(new Rectangle(p.X - d / 2, p.Y - d / 2, d, d)); Invalidate(); off.Stop(); off.Start(); }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics; g.Clear(Hole); // no smoothing: half-covered pixels would keep the hole's colour
        using (Pen p = new Pen(Theme.Accent, Theme.S(3))) { Rectangle r = Rectangle.Inflate(ClientRectangle, -Theme.S(3), -Theme.S(3)); g.DrawEllipse(p, r); r.Inflate(-Theme.S(10), -Theme.S(10)); g.DrawEllipse(p, r); }
    }
    protected override void Dispose(bool d) { if (d) off.Dispose(); base.Dispose(d); }
}
}
