using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// The hand-off picker: a card that opens under a chat's hand-off button. One row per account that can
// be opened, with its icon, both limits and when they reset, the suggested one on top; a line at the
// bottom says what the move will carry. Click a row (or arrow keys and Enter) to move the chat there.
class HandoffPicker : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    class Row
    {
        public int n; public string name; public Bitmap icon;
        public bool suggested, own, full; public string fullWhy;
        public double five, week, fiveReset, weekReset;
        public Rectangle r;
    }

    readonly float S;
    readonly string sid, chatTitle;
    readonly List<Row> rows = new List<Row>();
    readonly string noteLine; readonly bool noteReady;
    readonly Font fHead, fSub, fName, fSmall, fTiny, fBadge, fGlyph;
    int hover = -1;
    bool done;

    int P(float v) { return (int)Math.Round(v * S); }

    public HandoffPicker(string sid)
    {
        this.sid = sid;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Ui.Card; KeyPreview = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        fHead = new Font("Segoe UI Semibold", 10.5f);
        fSub = new Font("Segoe UI", 8.5f);
        fName = new Font("Segoe UI Semibold", 10f);
        fSmall = new Font("Segoe UI", 8.5f);
        fTiny = new Font("Segoe UI", 7.5f);
        fBadge = new Font("Segoe UI Semibold", 7f);
        fGlyph = new Font("Segoe MDL2 Assets", 9f);

        var state = ClaudeTray.State;
        double now = Ui.Now();
        var st = Accounts.Load();
        var infos = Accounts.All();

        // Which account the chat is on now, and its title.
        int ownN = -1; string title = null;
        foreach (Dictionary<string, object> a in ClaudeTray.Arr(state, "accounts"))
            foreach (var kv in a)
            {
                var items = kv.Value as object[];
                if (items == null || kv.Key == "windows") continue;
                foreach (var o in items)
                {
                    var it = o as Dictionary<string, object>;
                    if (it != null && it.ContainsKey("sessionId") && it["sessionId"] as string == sid)
                    {
                        if (kv.Key != "pinned" || ownN < 0) ownN = Convert.ToInt32(a["n"]);
                        if (title == null) title = it.ContainsKey("title") ? it["title"] as string : null;
                    }
                }
            }
        chatTitle = title;
        // Suggested for this chat: the same pick the automatic move at the limit makes, never the chat's own account.
        var hint = ClaudeTray.SuggestAccount(state, now, ownN);
        int hinted = hint == null ? -1 : Convert.ToInt32(hint["n"]);

        foreach (Dictionary<string, object> a in ClaudeTray.Arr(state, "accounts"))
        {
            int n = Convert.ToInt32(a["n"]);
            if (n >= 99 || !(a["canStart"] is bool && (bool)a["canStart"])) continue;
            var row = new Row
            {
                n = n, name = (string)a["name"], suggested = n == hinted, own = n == ownN,
                five = ClaudeTray.Pct(a, "five_hour"), week = ClaudeTray.Pct(a, "seven_day"),
                fiveReset = ClaudeTray.ResetAt(a, "five_hour"), weekReset = ClaudeTray.ResetAt(a, "seven_day"),
            };
            // A window that already reset is empty again.
            if (!double.IsNaN(row.fiveReset) && row.fiveReset <= now) { row.five = 0; row.fiveReset = double.NaN; }
            if (!double.IsNaN(row.weekReset) && row.weekReset <= now) { row.week = 0; row.weekReset = double.NaN; }
            double cut = a["cutResetsAt"] == null ? double.NaN : ClaudeTray.Num(a["cutResetsAt"]);
            if (!double.IsNaN(cut) && cut > now) { row.full = true; row.fullWhy = "5-hour limit reached · back in " + Ui.Dur(cut - now); }
            else if (row.five >= 100) { row.full = true; row.fullWhy = "5-hour limit reached" + (double.IsNaN(row.fiveReset) ? "" : " · back in " + Ui.Dur(row.fiveReset - now)); }
            else if (row.week >= 100) { row.full = true; row.fullWhy = "Weekly limit reached" + (double.IsNaN(row.weekReset) ? "" : " · back in " + Ui.Dur(row.weekReset - now)); }
            var info = infos.FirstOrDefault(x => x.n == n) ?? new AccountInfo { n = n };
            try { row.icon = Accounts.Render(info, P(30)); } catch { }
            rows.Add(row);
        }
        // Suggested first, then the others, then full ones, and the chat's own account last.
        var ordered = rows.OrderBy(r => r.own ? 3 : r.full ? 2 : r.suggested ? 0 : 1).ThenBy(r => Accounts.Rank(st, r.n)).ToList();
        rows.Clear(); rows.AddRange(ordered);

        if (rows.Count > 0)
        {
            string why = null;
            try { why = Data.WhyNotAskForNote(sid); } catch { why = "error"; }
            if (why == null) noteLine = "The chat writes a short hand-off note first, then moves.";
            else if (Data.NoteFor(sid) != null) { noteLine = "Hand-off note ready: the new chat gets it."; noteReady = true; }
            else noteLine = "The new chat gets a summary of this one.";
        }

        Layout0();
        hover = rows.FindIndex(r => !r.full);
    }

    const int HeadH = 58, RowH = 64, FootH = 38, Pad = 8, W = 340;

    void Layout0()
    {
        int y = P(HeadH);
        foreach (var r in rows) { r.r = new Rectangle(P(Pad), y, P(W) - P(Pad) * 2, P(RowH)); y += P(RowH) + P(2); }
        if (rows.Count == 0) y += P(44);
        y += P(Pad) - P(2);
        if (noteLine != null) y += P(FootH);
        ClientSize = new Size(P(W), y);
    }

    public void ShowAt(Control owner, Rectangle anchor)
    {
        var scr = owner.RectangleToScreen(anchor);
        var wa = Screen.FromRectangle(scr).WorkingArea;
        int x = Math.Max(wa.Left + P(4), Math.Min(scr.Right - Width, wa.Right - Width - P(4)));
        int y = scr.Bottom + P(4);
        if (y + Height > wa.Bottom - P(4)) y = Math.Max(wa.Top + P(4), scr.Top - Height - P(4));   // no room below: open above
        Location = new Point(x, y);
        Owner = owner.FindForm();   // an owned window always sits above its owner, even as the panel re-raises itself
        Panel.MenusOpen++;
        // Shown once the click that opened it has finished, so the panel taking that click doesn't close it.
        owner.BeginInvoke((Action)(() => { shownAt = DateTime.UtcNow; Show(); Activate(); }));
    }
    DateTime shownAt = DateTime.MaxValue;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; }   // tool window, drop shadow
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1;
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Focus bouncing back to the panel right as it opens isn't "clicked away": take it back.
        if ((DateTime.UtcNow - shownAt).TotalMilliseconds < 400) { BeginInvoke((Action)Activate); return; }
        BeginInvoke((Action)Close);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Panel.MenusOpen = Math.Max(0, Panel.MenusOpen - 1);
        foreach (var r in rows) if (r.icon != null) r.icon.Dispose();
        foreach (var f in new[] { fHead, fSub, fName, fSmall, fTiny, fBadge, fGlyph }) f.Dispose();
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys k)
    {
        if (k == Keys.Escape) { Close(); return true; }
        if (k == Keys.Down || k == Keys.Up)
        {
            int step = k == Keys.Down ? 1 : -1, i = hover;
            for (int t = 0; t < rows.Count; t++)
            {
                i = ((i < 0 ? (step > 0 ? -1 : 0) : i) + step + rows.Count) % rows.Count;
                if (!rows[i].full) { hover = i; Invalidate(); break; }
            }
            return true;
        }
        if (k == Keys.Enter) { Pick(hover); return true; }
        return base.ProcessCmdKey(ref msg, k);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = rows.FindIndex(r => r.r.Contains(e.Location));
        Cursor = h >= 0 && !rows[h].full ? Cursors.Hand : Cursors.Default;
        if (h >= 0 && rows[h].full) h = -1;
        if (h != hover && h >= 0) { hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Pick(rows.FindIndex(r => r.r.Contains(e.Location)));
        base.OnMouseUp(e);
    }

    void Pick(int i)
    {
        if (done || i < 0 || i >= rows.Count || rows[i].full) return;
        done = true;
        int n = rows[i].n;
        Close();
        ClaudeTray.HandoffAfterNote(sid, n);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Ui.Card);
        using (var pen = new Pen(Ui.Line)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        // header
        int x = P(Pad) + P(8);
        TextRenderer.DrawText(g, "", fGlyph, new Rectangle(x, P(12), P(16), P(20)), Ui.Blue, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Continue on another account", fHead, new Point(x + P(22), P(12)), Ui.Text, TextFormatFlags.NoPadding);
        string sub = string.IsNullOrWhiteSpace(chatTitle) ? "Opens a new chat there with this one handed over" : chatTitle;
        TextRenderer.DrawText(g, sub, fSub, new Rectangle(x, P(34), Width - x - P(14), P(16)), Ui.Dim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        using (var pen = new Pen(Ui.Line)) g.DrawLine(pen, P(Pad), P(HeadH) - P(4), Width - P(Pad), P(HeadH) - P(4));

        if (rows.Count == 0)
            TextRenderer.DrawText(g, "No other account can be opened. Add one in Accounts.", fSmall, new Rectangle(P(Pad), P(HeadH), Width - P(Pad) * 2, P(44)), Ui.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);

        for (int i = 0; i < rows.Count; i++) DrawAccount(g, rows[i], i == hover);

        if (noteLine != null)
        {
            int fy = Height - P(FootH);
            using (var pen = new Pen(Ui.Line)) g.DrawLine(pen, P(Pad), fy, Width - P(Pad), fy);
            var c = noteReady ? Ui.Ok : Ui.Dim;
            TextRenderer.DrawText(g, noteReady ? "" : "", fGlyph, new Rectangle(x, fy, P(16), P(FootH)), c, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, noteLine, fTiny, new Rectangle(x + P(22), fy, Width - x - P(36), P(FootH)), c, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    void DrawAccount(Graphics g, Row a, bool hov)
    {
        var r = a.r;
        if (hov)
        {
            using (var p = Ui.Rounded(r, P(8))) using (var b = new SolidBrush(Color.FromArgb(44, 43, 40))) g.FillPath(b, p);
            using (var p = Ui.Rounded(r, P(8))) using (var pen = new Pen(Color.FromArgb(120, a.suggested ? Ui.Accent : Ui.Blue))) g.DrawPath(pen, p);
        }
        int alpha = a.full ? 110 : 255;
        Func<Color, Color> fade = c => Color.FromArgb(alpha, c);

        // icon
        var ir = new Rectangle(r.X + P(10), r.Y + (r.Height - P(30)) / 2, P(30), P(30));
        if (a.icon != null)
        {
            if (a.full) using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.4f });
                g.DrawImage(a.icon, ir, 0, 0, a.icon.Width, a.icon.Height, GraphicsUnit.Pixel, ia);
            }
            else g.DrawImage(a.icon, ir);
        }

        // name + badge
        int tx = ir.Right + P(12), right = r.Right - P(12);
        var ns = TextRenderer.MeasureText(g, a.name, fName, Size.Empty, TextFormatFlags.NoPadding);
        int nameMax = right - tx - P(84);
        TextRenderer.DrawText(g, a.name, fName, new Rectangle(tx, r.Y + P(9), Math.Min(ns.Width + P(2), nameMax), P(20)), a.full ? Ui.Faint : Ui.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        string badge = a.own ? "THIS CHAT" : a.suggested && !a.full ? "SUGGESTED" : null;
        if (badge != null)
        {
            var bs = TextRenderer.MeasureText(g, badge, fBadge, Size.Empty, TextFormatFlags.NoPadding);
            var br = new Rectangle(tx + Math.Min(ns.Width, nameMax) + P(8), r.Y + P(10), bs.Width + P(10), P(16));
            var bc = a.suggested ? Ui.Accent : Ui.Faint;
            using (var p = Ui.Rounded(br, P(8))) using (var b = new SolidBrush(Color.FromArgb(40, bc))) g.FillPath(b, p);
            TextRenderer.DrawText(g, badge, fBadge, br, bc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        if (hov && !a.full)
            TextRenderer.DrawText(g, "", fGlyph, new Rectangle(right - P(14), r.Y + P(8), P(16), P(20)), Ui.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // limits, or why it can't take the chat
        int ly = r.Y + P(34);
        if (a.full)
        {
            TextRenderer.DrawText(g, a.fullWhy, fTiny, new Point(tx, ly + P(2)), Ui.Bad, TextFormatFlags.NoPadding);
            return;
        }
        int colW = (right - tx - P(14)) / 2;
        DrawLimit(g, "5-hour", a.five, a.fiveReset, tx, ly, colW);
        DrawLimit(g, "Weekly", a.week, a.weekReset, tx + colW + P(14), ly, colW);
    }

    void DrawLimit(Graphics g, string label, double p, double reset, int x, int y, int w)
    {
        double now = Ui.Now();
        string l = label + (!double.IsNaN(reset) && reset > now ? " · " + Ui.Dur(reset - now) : "");
        TextRenderer.DrawText(g, l, fTiny, new Rectangle(x, y, w - P(30), P(14)), Ui.Dim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        string val = double.IsNaN(p) ? "?" : Math.Round(p) + "%";
        var vs = TextRenderer.MeasureText(g, val, fTiny, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, val, fTiny, new Point(x + w - vs.Width, y), double.IsNaN(p) ? Ui.Faint : Ui.BarColor(p), TextFormatFlags.NoPadding);
        var bar = new RectangleF(x, y + P(17), w, P(4));
        using (var path = Ui.Rounded(bar, P(2))) using (var b = new SolidBrush(Ui.Line)) g.FillPath(b, path);
        if (!double.IsNaN(p) && p > 0)
            using (var path = Ui.Rounded(new RectangleF(x, y + P(17), Math.Max(P(4), (float)(w * Math.Min(100, p) / 100)), P(4)), P(2)))
            using (var b = new SolidBrush(Ui.BarColor(p))) g.FillPath(b, path);
    }
}
