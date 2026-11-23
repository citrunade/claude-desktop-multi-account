using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// The small bar that lives on the taskbar: the five-hour figure and what's new or busy. It is its own
// window, so it stays put while the panel opens and closes above it. Drag it left or right along the
// taskbar; a plain click opens the panel (or, in "stays open" mode, opens / shrinks it).
class BarWindow : Form
{
    readonly Panel owner;

    public BarWindow(Panel p)
    {
        owner = p;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Bg;
        Text = "Claude Tracker bar";
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.SizeWE;   // it slides along the taskbar
        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) owner.BarDown(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: no taskbar button
            cp.ExStyle |= 0x08000000;    // WS_EX_NOACTIVATE: clicking it doesn't take the focus from your window
            return cp;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 3;   // small rounded corners
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        owner.PaintBar(e.Graphics, Width, Height);
        using (var pen = new Pen(Ui.Line)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x216)   // WM_MOVING: stay on the taskbar
        {
            var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
            owner.BarMoving(ref rc.L, ref rc.T, ref rc.R, ref rc.B);
            Marshal.StructureToPtr(rc, m.LParam, false);
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }
}
