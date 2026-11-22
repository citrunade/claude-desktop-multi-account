using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Claude Tracker: tray icon + a floating, always-on-top panel showing plan limits and the
// conversations that need you, for every Claude account. It also manages the accounts themselves.
// The panel has no title bar; drag it by its header, collapse it to a small pill
// with the – button, hide it with × (the tray icon brings it back).
//
//   Claude Tracker.exe                  the panel
//   Claude Tracker.exe --manage         the accounts window only
//   Claude Tracker.exe --setup N [--no-widget]   first-time setup, used by the installer
static class ClaudeTray
{
    public static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
    static NotifyIcon tray;
    static Icon currentIcon;
    static double lastCutReset;
    public static Panel panel;
    public static Dictionary<string, object> State;
    static bool fetching;

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        if (args.Length > 0 && args[0] == "--setup")
        {
            int count = 3;
            if (args.Length > 1) int.TryParse(args[1], out count);
            try { Accounts.Setup(Math.Max(1, Math.Min(9, count)), Array.IndexOf(args, "--no-widget") < 0); }
            catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
            return;
        }
        if (args.Length > 1 && args[0] == "--dump")   // for checking what it reads: writes the panel's data as JSON
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = Data.State(); var firstMs = sw.ElapsedMilliseconds; sw.Restart(); Data.State();
            st["_ms"] = new[] { firstMs, sw.ElapsedMilliseconds };
            File.WriteAllText(args[1], new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(st));
            return;
        }
        if (args.Length > 1 && args[0] == "--apply")   // rebuild one account from accounts.json (icon, launcher, shortcuts)
        {
            int n; int.TryParse(args[1], out n);
            var a = Accounts.Load().accounts.FirstOrDefault(x => x.n == n);
            try { if (a == null) throw new Exception("No account " + n + " in accounts.json"); Accounts.Apply(a); }
            catch (Exception e) { File.WriteAllText(Path.Combine(Dir, "apply-error.txt"), e.ToString()); Environment.Exit(1); }
            return;
        }
        if (args.Length > 0 && args[0] == "--unregister") { Accounts.UnregisterLimitsPlugin(); return; }
        if (args.Length > 1 && args[0] == "--snapshot")   // for checking the accounts window: draws it to a picture, never shows it
        {
            using (var f = new ManageForm { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false })
            {
                f.Show(); Application.DoEvents();
                using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(args[1]); }
            }
            return;
        }
        if (args.Length > 0 && args[0] == "--manage") { Application.Run(new ManageForm()); return; }

        bool first;
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClaudeTracker.Show");
        using (var mutex = new Mutex(true, "Local\\ClaudeTracker.Tray", out first))
        {
            // Running it again (for example from a shortcut) opens the running one's panel.
            if (!first) { showSignal.Set(); return; }

            tray = new NotifyIcon();
            tray.Text = "Claude Tracker";
            SetIcon(null, false);
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
            tray.BalloonTipClicked += (s, e) => { var a = pendingHandoff; pendingHandoff = null; if (a != null) a(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show panel", null, (s, e) => ShowPanel());
            menu.Items.Add("Refresh now", null, (s, e) => Fetch());
            menu.Items.Add("Manage accounts…", null, (s, e) => OpenManager());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;

            panel = new Panel();

            var poll = new System.Windows.Forms.Timer { Interval = 5000 };
            poll.Tick += (s, e) => Fetch();
            poll.Start();
            var sig = new System.Windows.Forms.Timer { Interval = 250 };
            sig.Tick += (s, e) => { if (showSignal.WaitOne(0)) ShowPanel(); };
            sig.Start();
            var tick = new System.Windows.Forms.Timer { Interval = 1000 };
            tick.Tick += (s, e) => { if (panel.Visible) panel.Invalidate(true); };
            tick.Start();

            Fetch();
            ShowPanel();
            Application.Run();
        }
    }

    // ---------- data ----------
    public static void Fetch()
    {
        if (fetching) return;
        fetching = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Dictionary<string, object> d = null;
            string err = null;
            try { d = Data.State(); }
            catch (Exception e) { err = e.Message; }
            try
            {
                panel.BeginInvoke((Action)(() =>
                {
                    fetching = false;
                    if (d == null) { tray.Text = "Claude Tracker: couldn't read Claude's files"; lastError = err; return; }
                    lastError = null;
                    State = d;
                    Apply(d);
                    panel.Board.Invalidate();
                    panel.Invalidate(true);
                }));
            }
            catch { fetching = false; }
        });
    }
    public static string lastError;

    public static object[] Arr(Dictionary<string, object> d, string k)
    {
        object o;
        return d != null && d.TryGetValue(k, out o) && o is object[] ? (object[])o : new object[0];
    }
    public static double Num(object o) { return o == null ? double.NaN : Convert.ToDouble(o); }

    public static Dictionary<string, object> Current()
    {
        Dictionary<string, object> cur = null;
        foreach (Dictionary<string, object> a in Arr(State, "accounts"))
        {
            object c;
            if (a.TryGetValue("current", out c) && c is bool && (bool)c) return a;
            if (cur == null) cur = a;
        }
        return cur;
    }

    public static double Pct(Dictionary<string, object> acct, string kind)
    {
        if (acct == null) return double.NaN;
        foreach (Dictionary<string, object> w in Arr(acct, "windows"))
            if ((string)w["kind"] == kind) return Num(w["percent"]);
        return double.NaN;
    }

    static void Apply(Dictionary<string, object> d)
    {
        var cur = Current();
        double five = Pct(cur, "five_hour"), week = Pct(cur, "seven_day");
        int unseen = Arr(d, "unseen").Length, waiting = Arr(d, "waiting").Length;
        int working = Arr(d, "working").Length, cut = Arr(d, "cut").Length;
        SetIcon(double.IsNaN(five) ? (double?)null : five, unseen > 0);
        string tip = (double.IsNaN(five) ? "5h ?" : "5h " + Math.Round(five) + "%")
                   + (double.IsNaN(week) ? "" : " · wk " + Math.Round(week) + "%")
                   + "\n" + unseen + " new · " + waiting + " seen · " + working + " working · " + cut + " cut";
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

        // One alert when the limit that cut conversations resets (Claude itself alerts on replies).
        double reset = d["cutResetsAt"] == null ? 0 : Num(d["cutResetsAt"]);
        double now = Num(d["now"]);
        if (reset > 0 && lastCutReset == reset && reset <= now && cut > 0)
        {
            tray.ShowBalloonTip(8000, "Limit has reset", cut + " cut conversation" + (cut == 1 ? "" : "s") + " can resume", ToolTipIcon.Info);
            lastCutReset = -1;
        }
        else if (reset > now) lastCutReset = reset;

        AutoHandoff(d, now);
    }

    // ---------- hand-off when a conversation hits its limit ----------
    static Action pendingHandoff;
    static HashSet<string> handedOff;
    static string HandedOffFile { get { return Path.Combine(Accounts.TrackerDir, "auto-handoffs.json"); } }

    static void AutoHandoff(Dictionary<string, object> d, double now)
    {
        string mode = Accounts.Load().autoHandoff ?? "ask";
        if (mode == "off") return;
        if (handedOff == null)
        {
            handedOff = new HashSet<string>();
            try { foreach (var s in File.ReadAllLines(HandedOffFile)) if (s.Trim().Length > 0) handedOff.Add(s.Trim()); } catch { }
        }
        foreach (Dictionary<string, object> acct in Arr(d, "accounts"))
        {
            int from = Convert.ToInt32(acct["n"]);
            foreach (var cat in new[] { "cut", "unseen", "waiting", "pinned" })
                foreach (Dictionary<string, object> it in Arr(acct, cat))
                {
                    if (!(it.ContainsKey("limited") && it["limited"] is bool && (bool)it["limited"])) continue;
                    var sid = (string)it["sessionId"];
                    if (handedOff.Contains(sid)) continue;
                    // Only fresh ones: a limit hit more than 15 minutes ago was already seen.
                    if (now - Num(it["lastActivity"]) > 15 * 60) { Remember(sid); continue; }
                    var to = BestAccount(d, from, now);
                    if (to == null) continue;   // nowhere better to go right now; try again next refresh
                    Remember(sid);
                    int n = Convert.ToInt32(to["n"]);
                    string title = (string)it["title"], toName = (string)to["name"], fromName = (string)acct["name"];
                    if (mode == "auto")
                    {
                        Handoff(sid, n);
                        tray.ShowBalloonTip(6000, "Moved to " + toName, "\"" + title + "\" hit " + fromName + "'s limit. It's ready in a new chat on " + toName + ": press Enter there to continue.", ToolTipIcon.Info);
                    }
                    else
                    {
                        pendingHandoff = () => Handoff(sid, n);
                        tray.ShowBalloonTip(15000, fromName + " hit its limit", "\"" + title + "\" stopped. Click here to continue it on " + toName + ".", ToolTipIcon.Info);
                    }
                    return;   // one at a time
                }
        }
    }

    static void Remember(string sid)
    {
        handedOff.Add(sid);
        try { File.WriteAllLines(HandedOffFile, handedOff.ToArray()); } catch { }
    }

    // The account with the most room left (by weekly, 5-hour or both, as chosen in settings),
    // skipping any account past the chosen weekly / 5-hour ceiling.
    static Dictionary<string, object> BestAccount(Dictionary<string, object> d, int from, double now)
    {
        var st = Accounts.Load();
        string by = st.handoffPickBy ?? "both";
        Dictionary<string, object> best = null; double bestScore = double.MaxValue;
        foreach (Dictionary<string, object> a in Arr(d, "accounts"))
        {
            int n = Convert.ToInt32(a["n"]);
            if (n == from || n == 99 || !(a["canStart"] is bool && (bool)a["canStart"])) continue;
            if (a["cutResetsAt"] != null && Num(a["cutResetsAt"]) > now) continue;
            double five = Pct(a, "five_hour"), week = Pct(a, "seven_day");
            if (five >= Math.Min(100, st.handoffMaxFive) || week >= Math.Min(100, st.handoffMaxWeekly)) continue;
            // No reading yet counts as half used: better than a busy account, worse than a known quiet one.
            double f = double.IsNaN(five) ? 50 : five, w = double.IsNaN(week) ? 50 : week;
            double score = by == "weekly" ? w + f / 1000 : by == "five" ? f + w / 1000 : Math.Max(f, w);
            if (score < bestScore) { bestScore = score; best = a; }
        }
        return best;
    }

    // ---------- icon ----------
    static void SetIcon(double? pct, bool unseen)
    {
        const int S = 32;
        using (var bmp = new Bitmap(S, S))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var path = Ui.Rounded(new RectangleF(0, 0, S - 1, S - 1), 8))
            using (var b = new SolidBrush(Ui.LimitColor(pct))) g.FillPath(b, path);
            string text = pct.HasValue ? Math.Round(pct.Value).ToString() : "?";
            using (var f = new Font("Segoe UI", text.Length >= 3 ? 13f : 18f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.White))
                g.DrawString(text, f, b, new RectangleF(0, 1, S, S), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            if (unseen)
            {
                using (var ring = new SolidBrush(Color.FromArgb(20, 20, 19))) g.FillEllipse(ring, S - 13, -1, 14, 14);
                using (var dot = new SolidBrush(Ui.Warn)) g.FillEllipse(dot, S - 11, 1, 10, 10);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            tray.Icon = icon;
            if (currentIcon != null) currentIcon.Dispose();
            currentIcon = icon;
        }
    }

    // ---------- panel ----------
    static void TogglePanel() { if (panel.Visible) panel.Hide(); else ShowPanel(); }
    public static void ShowPanel()
    {
        if (!panel.Visible) panel.Show();
        panel.TopMost = true;
        panel.Activate();
    }

    // Work that opens Claude runs off the panel's thread, then the panel refreshes.
    // The panel was just clicked, so it may hand the foreground to the window Claude opens.
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    static void Act(Func<bool> work, string failed)
    {
        AllowSetForegroundWindow(-1);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok = false;
            try { ok = work(); } catch { }
            try { panel.BeginInvoke((Action)(() => { if (!ok && failed != null) tray.ShowBalloonTip(3000, "Claude Tracker", failed, ToolTipIcon.Warning); Fetch(); })); } catch { }
        });
    }
    public static void StartAccount(int n) { Act(() => Data.StartConversation(n), "Couldn't open that account."); }
    public static void OpenConversation(string sid) { Act(() => Data.OpenConversation(sid), "Couldn't find that conversation in Claude."); }
    public static void Mark(string sid, string action) { Act(() => Marks.Set(sid, action), null); }
    public static void Handoff(string sid, int n)
    {
        string cwd;
        string text = null;
        try { text = Data.HandoffText(sid, out cwd); } catch { cwd = null; }
        if (text == null) { tray.ShowBalloonTip(3000, "Claude Tracker", "Couldn't read that conversation.", ToolTipIcon.Warning); return; }
        try { Clipboard.SetText(text); } catch { }   // backup in case the text doesn't land in the new chat
        try
        {
            var dir = Path.Combine(Data.ClaudeDir, "handoffs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, sid + ".md"), text);
        }
        catch { }
        string folder = cwd;
        Act(() => Data.StartConversation(n, text, folder), "Couldn't open that account.");
    }

    static ManageForm manager;
    public static void OpenManager()
    {
        if (manager == null || manager.IsDisposed)
        {
            manager = new ManageForm();
            manager.FormClosed += (s, e) => Fetch();
        }
        manager.Show(); manager.Activate();
    }

    public static void Open(string link)
    {
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); } catch { }
    }

    static void Quit()
    {
        tray.Visible = false;
        panel.SavePlace();
        Application.Exit();
    }
}

static class Ui
{
    public static readonly Color Bg = Color.FromArgb(22, 22, 21), Card = Color.FromArgb(31, 31, 29), Line = Color.FromArgb(48, 47, 44),
        Text = Color.FromArgb(236, 235, 230), Dim = Color.FromArgb(154, 151, 142), Faint = Color.FromArgb(110, 108, 100),
        Accent = Color.FromArgb(217, 119, 87), Ok = Color.FromArgb(127, 176, 105), Warn = Color.FromArgb(224, 178, 74),
        Bad = Color.FromArgb(224, 100, 90), Blue = Color.FromArgb(111, 168, 220), Hover = Color.FromArgb(40, 40, 37);

    public static Color LimitColor(double? p)
    {
        if (!p.HasValue || double.IsNaN(p.Value)) return Color.FromArgb(110, 108, 100);
        return p >= 90 ? Bad : p >= 70 ? Warn : Accent;
    }
    public static Color BarColor(double p) { return p >= 90 ? Bad : p >= 70 ? Warn : Ok; }

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static double Now() { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }
    public static string Ago(double ts)
    {
        double s = Math.Max(0, Now() - ts);
        if (s < 60) return "now";
        if (s < 3600) return (int)(s / 60) + " min";
        if (s < 86400) return (int)(s / 3600) + " h";
        return (int)(s / 86400) + " d";
    }
    public static string Dur(double sec)
    {
        double s = Math.Max(0, sec);
        int d = (int)(s / 86400); s -= d * 86400;
        int h = (int)(s / 3600); s -= h * 3600;
        int m = (int)Math.Ceiling(s / 60);
        if (d > 0) return d + "d " + h + "h";
        if (h > 0) return h + "h " + m + "m";
        return m + "m";
    }
}

// The floating window.
class Panel : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    public readonly float S;
    public readonly Board Board;
    readonly Control header;
    readonly ScrollHost host;
    bool collapsed;
    Size expandedSize;
    readonly string placeFile = Path.Combine(ClaudeTray.Dir, "window.txt");
    Rectangle btnCollapse, btnClose, btnGear;
    string hoverBtn;

    public Panel()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Bg;
        DoubleBuffered = true;
        Text = "Claude Tracker";
        expandedSize = new Size(P(380), P(620));
        Size = expandedSize;
        Padding = new Padding(1);

        header = new DoubleBufferedControl { Dock = DockStyle.Top, Height = P(40), BackColor = Ui.Bg };
        header.Paint += PaintHeader;
        header.MouseDown += HeaderDown;
        header.MouseMove += (s, e) => { string hb = btnCollapse.Contains(e.Location) ? "c" : btnClose.Contains(e.Location) ? "x" : btnGear.Contains(e.Location) ? "g" : null; if (hb != hoverBtn) { hoverBtn = hb; header.Invalidate(); } header.Cursor = hb != null ? Cursors.Hand : Cursors.Default; };
        header.MouseLeave += (s, e) => { hoverBtn = null; header.Invalidate(); };

        host = new ScrollHost { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        Board = new Board(this) { Location = new Point(0, 0) };
        host.Controls.Add(Board);
        host.Resize += (s, e) => Board.Width = host.ClientSize.Width;
        Board.SizeChanged += (s, e) => host.Clamp();
        Board.MouseWheel += (s, e) => host.ScrollBy(e.Delta / 2);
        Board.Width = expandedSize.Width - P(20);

        Controls.Add(host);
        Controls.Add(header);
        LoadPlace();
    }

    public int P(float v) { return (int)Math.Round(v * S); }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: no taskbar button
            cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // Windows 11 rounded corners
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        int dark = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }

    protected override bool ShowWithoutActivation { get { return false; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Ui.Line)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    void PaintHeader(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int w = header.Width, h = header.Height;
        if (collapsed) { PaintPill(g, w, h); return; }
        using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, P(14), h / 2 - P(4), P(8), P(8));
        using (var f = new Font("Segoe UI Semibold", 10.5f))
            TextRenderer.DrawText(g, "Claude Code", f, new Point(P(28), h / 2 - P(10)), Ui.Text);
        btnClose = new Rectangle(w - P(36), h / 2 - P(13), P(26), P(26));
        btnCollapse = new Rectangle(w - P(64), h / 2 - P(13), P(26), P(26));
        btnGear = new Rectangle(w - P(92), h / 2 - P(13), P(26), P(26));
        DrawHeaderBtn(g, btnCollapse, "c");
        DrawHeaderBtn(g, btnClose, "x");
        DrawHeaderBtn(g, btnGear, "g");
        using (var pen = new Pen(Ui.Line)) g.DrawLine(pen, 0, h - 1, w, h - 1);
    }

    void DrawHeaderBtn(Graphics g, Rectangle r, string which)
    {
        if (hoverBtn == which) using (var p = Ui.Rounded(r, P(6))) using (var b = new SolidBrush(Ui.Hover)) g.FillPath(b, p);
        using (var pen = new Pen(hoverBtn == which ? Ui.Text : Ui.Dim, 1.4f * S))
        {
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, k = P(5);
            if (which == "g")
            {
                using (var f = new Font("Segoe MDL2 Assets", 9.5f))
                    TextRenderer.DrawText(g, "\uE713", f, r, hoverBtn == which ? Ui.Text : Ui.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else if (which == "c") { if (collapsed) { g.DrawLine(pen, cx - k, cy + P(2), cx, cy - P(3)); g.DrawLine(pen, cx, cy - P(3), cx + k, cy + P(2)); } else g.DrawLine(pen, cx - k, cy, cx + k, cy); }
            else { g.DrawLine(pen, cx - P(4), cy - P(4), cx + P(4), cy + P(4)); g.DrawLine(pen, cx + P(4), cy - P(4), cx - P(4), cy + P(4)); }
        }
    }

    void PaintPill(Graphics g, int w, int h)
    {
        var st = ClaudeTray.State;
        var cur = ClaudeTray.Current();
        double five = ClaudeTray.Pct(cur, "five_hour");
        int unseen = ClaudeTray.Arr(st, "unseen").Length, working = ClaudeTray.Arr(st, "working").Length, cut = ClaudeTray.Arr(st, "cut").Length;
        int x = P(12);
        using (var b = new SolidBrush(Ui.LimitColor(double.IsNaN(five) ? (double?)null : five)))
        using (var p = Ui.Rounded(new RectangleF(x, h / 2 - P(10), P(34), P(20)), P(10))) g.FillPath(b, p);
        using (var f = new Font("Segoe UI Semibold", 8.5f))
            TextRenderer.DrawText(g, double.IsNaN(five) ? "?" : Math.Round(five) + "%", f, new Rectangle(x, h / 2 - P(10), P(34), P(20)), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        x += P(42);
        var parts = new List<KeyValuePair<string, Color>>();
        if (unseen > 0) parts.Add(new KeyValuePair<string, Color>(unseen + " new", Ui.Warn));
        if (working > 0) parts.Add(new KeyValuePair<string, Color>(working + " working", Ui.Blue));
        if (cut > 0) parts.Add(new KeyValuePair<string, Color>(cut + " cut", Ui.Bad));
        if (parts.Count == 0) parts.Add(new KeyValuePair<string, Color>("all quiet", Ui.Dim));
        using (var f = new Font("Segoe UI", 9f))
            foreach (var kv in parts)
            {
                var sz = TextRenderer.MeasureText(g, kv.Key, f, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, kv.Key, f, new Point(x, h / 2 - sz.Height / 2), kv.Value, TextFormatFlags.NoPadding);
                x += sz.Width + P(10);
            }
        btnCollapse = new Rectangle(w - P(34), h / 2 - P(13), P(26), P(26));
        btnClose = Rectangle.Empty; btnGear = Rectangle.Empty;
        DrawHeaderBtn(g, btnCollapse, "c");
    }

    void HeaderDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (btnCollapse.Contains(e.Location)) { SetCollapsed(!collapsed); return; }
        if (btnClose.Contains(e.Location)) { SavePlace(); Hide(); return; }
        if (btnGear.Contains(e.Location)) { ClaudeTray.OpenManager(); return; }
        var start = Location;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // drag like a title bar
        if (collapsed && Location == start) SetCollapsed(false); // a click on the pill opens it
        SavePlace();
    }

    void SetCollapsed(bool c)
    {
        if (c == collapsed) return;
        var br = new Point(Right, Bottom); // keep the bottom-right corner in place
        collapsed = c;
        host.Visible = !c;
        if (c) { expandedSize = Size; Size = new Size(P(270), P(40)); header.Height = P(40); }
        else Size = expandedSize;
        Location = new Point(br.X - Width, br.Y - Height);
        KeepOnScreen();
        header.Invalidate();
        SavePlace();
    }

    void KeepOnScreen()
    {
        var wa = Screen.FromPoint(Location).WorkingArea;
        Location = new Point(Math.Max(wa.Left, Math.Min(Left, wa.Right - Width)), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height)));
    }

    void LoadPlace()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - P(12), wa.Bottom - Height - P(12));
        try
        {
            var parts = File.ReadAllText(placeFile).Split(',');
            int right = int.Parse(parts[0]), bottom = int.Parse(parts[1]);
            bool c = parts.Length > 2 && parts[2] == "1";
            Location = new Point(right - Width, bottom - Height);
            KeepOnScreen();
            if (c) SetCollapsed(true);
        }
        catch { }
    }

    public void SavePlace()
    {
        try { File.WriteAllText(placeFile, Right + "," + Bottom + "," + (collapsed ? "1" : "0")); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}

class DoubleBufferedControl : Control
{
    public DoubleBufferedControl() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
}

// Holds the board and scrolls it with the mouse wheel, without drawing a scrollbar.
class ScrollHost : Control
{
    int offset;
    public ScrollHost() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    public void ScrollBy(int delta) { offset += delta; Clamp(); }
    public void Clamp()
    {
        if (Controls.Count == 0) return;
        var b = Controls[0];
        int min = Math.Min(0, ClientSize.Height - b.Height);
        offset = Math.Max(min, Math.Min(0, offset));
        if (b.Top != offset) b.Top = offset;
    }
    protected override void OnMouseWheel(MouseEventArgs e) { ScrollBy(e.Delta / 2); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Clamp(); }
}

// Draws all accounts and conversations; one control, hit-tested by hand.
class Board : DoubleBufferedControl
{
    readonly Panel owner;
    readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();
    readonly List<Rectangle> hoverables = new List<Rectangle>();
    Point mouse = new Point(-1, -1);
    Font fTitle, fTitleBold, fSmall, fTiny, fName, fLabel;

    public Board(Panel owner)
    {
        this.owner = owner;
        BackColor = Ui.Bg;
        fName = new Font("Segoe UI Semibold", 10f);
        fTitle = new Font("Segoe UI", 9.5f);
        fTitleBold = new Font("Segoe UI Semibold", 9.5f);
        fSmall = new Font("Segoe UI", 8.5f);
        fTiny = new Font("Segoe UI", 7.5f);
        fLabel = new Font("Segoe UI Semibold", 7f);
        Height = 100;
    }

    int P(float v) { return owner.P(v); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = e.Location;
        bool overHit = false;
        foreach (var h in hits) if (h.Key.Contains(mouse)) { overHit = true; break; }
        Cursor = overHit ? Cursors.Hand : Cursors.Default;
        string t = null;
        foreach (var kv in tips) if (kv.Key.Contains(mouse)) { t = kv.Value; break; }
        if (t != tipShown) { tipShown = t; if (t == null) tip.Hide(this); else tip.Show(t, this, mouse.X + 12, mouse.Y + 18, 1500); }
        Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { mouse = new Point(-1, -1); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Parent.Focus();
        if (e.Button != MouseButtons.Left) return;
        for (int i = hits.Count - 1; i >= 0; i--)
            if (hits[i].Key.Contains(e.Location)) { hits[i].Value(); return; }
    }

    static readonly string[][] Groups = {
        new[] { "pinned", "PINNED" }, new[] { "working", "WORKING" }, new[] { "unseen", "NEW, NOT SEEN" },
        new[] { "waiting", "SEEN, WAITING FOR YOU" }, new[] { "cut", "CUT BY 5-HOUR LIMIT" } };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        hits.Clear();
        tips.Clear();
        var st = ClaudeTray.State;
        int y = P(10), x0 = P(10), w = Width - P(20);
        if (st == null)
        {
            TextRenderer.DrawText(g, "Loading…", fSmall, new Point(x0 + P(4), y), Ui.Dim);
            return;
        }
        foreach (Dictionary<string, object> a in ClaudeTray.Arr(st, "accounts"))
        {
            int top = y;
            // measure pass: draw card background after computing height -> draw into list then paint
            int h = MeasureAccount(g, a, w);
            using (var p = Ui.Rounded(new RectangleF(x0, top, w, h), P(10)))
            {
                using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.Line)) g.DrawPath(pen, p);
            }
            DrawAccount(g, a, x0 + P(12), top + P(10), w - P(24));
            y = top + h + P(10);
        }
        int want = y + P(4);
        if (Height != want) Height = want;
    }

    int MeasureAccount(Graphics g, Dictionary<string, object> a, int w)
    {
        int h = P(10) + P(22) + P(30);
        if (AsOf(a) != null) h += P(14);
        bool any = false;
        foreach (var gr in Groups)
        {
            var items = ClaudeTray.Arr(a, gr[0]);
            if (items.Length == 0) continue;
            any = true;
            h += P(22);
            foreach (Dictionary<string, object> it in items) h += RowHeight(it, gr[0]);
        }
        if (!any) h += P(22);
        return h + P(6);
    }

    int RowHeight(Dictionary<string, object> it, string key)
    {
        string k = key == "pinned" ? (it["state"] as string ?? "idle") : key;
        return P(k != "working" && it["detail"] != null ? 52 : 38);
    }

    string AsOf(Dictionary<string, object> a)
    {
        object src = a["source"];
        if (Convert.ToInt32(a["n"]) >= 99) return null;
        double asof = a["asOf"] == null ? double.NaN : ClaudeTray.Num(a["asOf"]);
        if (src == null) return "no reading yet: appears after this account's next reply";
        if ((string)src == "app" && !double.IsNaN(asof)) return "older reading from the app · " + Ui.Ago(asof) + " ago";
        if ((string)src == "live" && !double.IsNaN(asof) && Ui.Now() - asof > 1800) return "as of " + Ui.Ago(asof) + " ago";
        return null;
    }

    void DrawAccount(Graphics g, Dictionary<string, object> a, int x, int y, int w)
    {
        string name = (string)a["name"];
        TextRenderer.DrawText(g, name, fName, new Point(x, y), Ui.Text, TextFormatFlags.NoPadding);
        int nx = x + TextRenderer.MeasureText(g, name, fName, Size.Empty, TextFormatFlags.NoPadding).Width + P(8);
        object cur;
        if (a.TryGetValue("current", out cur) && cur is bool && (bool)cur)
        {
            var r = new Rectangle(nx, y + P(2), P(46), P(16));
            using (var p = Ui.Rounded(r, P(8))) using (var b = new SolidBrush(Color.FromArgb(45, 58, 40))) g.FillPath(b, p);
            TextRenderer.DrawText(g, "IN USE", fLabel, r, Ui.Ok, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        int n = Convert.ToInt32(a["n"]);
        bool canStart = a["canStart"] is bool && (bool)a["canStart"];
        if (n < 99)
        {
            var br = new Rectangle(x + w - P(58), y - P(2), P(58), P(22));
            bool hov = canStart && br.Contains(mouse);
            using (var p = Ui.Rounded(br, P(6)))
            {
                if (hov) using (var b = new SolidBrush(Ui.Hover)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.Line)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, "+ New", fSmall, br, canStart ? (hov ? Ui.Text : Ui.Dim) : Ui.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (canStart) { int nn = n; hits.Add(new KeyValuePair<Rectangle, Action>(br, () => { ClaudeTray.StartAccount(nn); })); }
        }
        y += P(26);

        // limits
        var wins = ClaudeTray.Arr(a, "windows");
        int colW = (w - P(14)) / 2, cx = x;
        foreach (Dictionary<string, object> win in wins)
        {
            double p = ClaudeTray.Num(win["percent"]);
            double rs = win["resetsAt"] == null ? double.NaN : ClaudeTray.Num(win["resetsAt"]);
            string label = (string)win["label"] + (!double.IsNaN(rs) && rs > Ui.Now() ? " · " + Ui.Dur(rs - Ui.Now()) : "");
            TextRenderer.DrawText(g, label, fTiny, new Point(cx, y), Ui.Dim, TextFormatFlags.NoPadding);
            string val = double.IsNaN(p) ? "?" : Math.Round(p) + "%";
            var vs = TextRenderer.MeasureText(g, val, fTiny, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, val, fTiny, new Point(cx + colW - vs.Width, y), double.IsNaN(p) ? Ui.Faint : Ui.BarColor(p), TextFormatFlags.NoPadding);
            var bar = new RectangleF(cx, y + P(16), colW, P(3));
            using (var path = Ui.Rounded(bar, P(1.5f))) using (var b = new SolidBrush(Ui.Line)) g.FillPath(b, path);
            if (!double.IsNaN(p) && p > 0)
                using (var path = Ui.Rounded(new RectangleF(cx, y + P(16), Math.Max(P(3), (float)(colW * Math.Min(100, p) / 100)), P(3)), P(1.5f)))
                using (var b = new SolidBrush(Ui.BarColor(p))) g.FillPath(b, path);
            cx += colW + P(14);
        }
        y += P(30);
        string asof = AsOf(a);
        if (asof != null) { TextRenderer.DrawText(g, asof, fTiny, new Point(x, y - P(6)), Ui.Faint, TextFormatFlags.NoPadding); y += P(14); }

        bool any = false;
        foreach (var gr in Groups)
        {
            var items = ClaudeTray.Arr(a, gr[0]);
            if (items.Length == 0) continue;
            any = true;
            TextRenderer.DrawText(g, gr[1] + "  " + items.Length, fLabel, new Point(x, y + P(4)), Ui.Faint, TextFormatFlags.NoPadding);
            if (gr[0] == "cut" && a["cutResetsAt"] != null)
            {
                double r = ClaudeTray.Num(a["cutResetsAt"]);
                string sub = r > Ui.Now() ? "resume in " + Ui.Dur(r - Ui.Now()) : "ready to resume";
                var ss = TextRenderer.MeasureText(g, sub, fTiny, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, sub, fTiny, new Point(x + w - ss.Width, y + P(4)), Ui.Dim, TextFormatFlags.NoPadding);
            }
            y += P(22);
            foreach (Dictionary<string, object> it in items) y += DrawRow(g, it, gr[0], x - P(6), y, w + P(12));
        }
        if (!any) TextRenderer.DrawText(g, "No active conversations", fSmall, new Point(x, y + P(2)), Ui.Faint, TextFormatFlags.NoPadding);
    }

    static readonly Font fIcon = new Font("Segoe MDL2 Assets", 9f), fIconSmall = new Font("Segoe MDL2 Assets", 7f);
    void DrawIconBtn(Graphics g, Rectangle r, string kind, Action click)
    {
        bool hov = r.Contains(mouse);
        using (var p = Ui.Rounded(r, P(6)))
        {
            using (var b = new SolidBrush(hov ? Color.FromArgb(58, 57, 53) : Ui.Card)) g.FillPath(b, p);
            using (var pen = new Pen(Ui.Line)) g.DrawPath(pen, p);
        }
        string glyph = kind == "done" ? "\uE73E" : kind == "pin" ? "\uE718" : kind == "unpin" ? "\uE77A" : "\uE8AB";
        Color c = kind == "done" ? Ui.Ok : kind == "handoff" ? Ui.Blue : Ui.Accent;
        TextRenderer.DrawText(g, glyph, fIcon, r, hov ? c : Ui.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        hits.Add(new KeyValuePair<Rectangle, Action>(r, click));
        tips.Add(new KeyValuePair<Rectangle, string>(r, kind == "done" ? "Mark as finished" : kind == "pin" ? "Pin" : kind == "unpin" ? "Unpin" : "Continue on another account"));
    }

    readonly List<KeyValuePair<Rectangle, string>> tips = new List<KeyValuePair<Rectangle, string>>();
    readonly ToolTip tip = new ToolTip();
    string tipShown;

    void ShowHandoffMenu(string sid, Rectangle at)
    {
        var menu = new ContextMenuStrip();
        foreach (Dictionary<string, object> a in ClaudeTray.Arr(ClaudeTray.State, "accounts"))
        {
            int n = Convert.ToInt32(a["n"]);
            if (n >= 99 || !(a["canStart"] is bool && (bool)a["canStart"])) continue;
            int nn = n;
            menu.Items.Add("Continue in a new chat on " + (string)a["name"], null, (s, e) => ClaudeTray.Handoff(sid, nn));
        }
        if (menu.Items.Count == 0) menu.Items.Add("No accounts can be opened").Enabled = false;
        menu.Show(this, new Point(at.Left, at.Bottom));
    }

    int DrawRow(Graphics g, Dictionary<string, object> it, string key, int x, int y, int w)
    {
        int h = RowHeight(it, key);
        var r = new Rectangle(x, y, w, h - P(2));
        string link = it["open"] as string;
        if (link != null)
        {
            if (r.Contains(mouse)) using (var p = Ui.Rounded(r, P(7))) using (var b = new SolidBrush(Ui.Hover)) g.FillPath(b, p);
            string osid = it.ContainsKey("sessionId") ? it["sessionId"] as string : null;
            hits.Add(new KeyValuePair<Rectangle, Action>(r, () => { if (osid != null) ClaudeTray.OpenConversation(osid); else ClaudeTray.Open(link); }));
        }
        string realKey = key == "pinned" ? (it["state"] as string ?? "idle") : key;
        bool rowHover = r.Contains(mouse);
        string sid = it.ContainsKey("sessionId") ? it["sessionId"] as string : null;
        // dot
        int dx = x + P(9), dy = y + P(11);
        var dotR = new RectangleF(dx, dy, P(7), P(7));
        if (realKey == "idle") using (var pen = new Pen(Ui.Faint, 1.5f * owner.S)) g.DrawEllipse(pen, dotR);
        else if (realKey == "working")
        {
            int alpha = (int)(155 + 100 * Math.Abs(Math.Sin(Ui.Now() * 2.2)));
            using (var b = new SolidBrush(Color.FromArgb(alpha, Ui.Blue))) g.FillEllipse(b, dotR);
        }
        else if (realKey == "unseen")
        {
            using (var b = new SolidBrush(Color.FromArgb(50, Ui.Warn))) g.FillEllipse(b, dx - P(3), dy - P(3), P(13), P(13));
            using (var b = new SolidBrush(Ui.Warn)) g.FillEllipse(b, dotR);
        }
        else if (realKey == "waiting") using (var pen = new Pen(Ui.Warn, 1.5f * owner.S)) g.DrawEllipse(pen, dotR);
        else using (var b = new SolidBrush(Ui.Bad)) g.FillEllipse(b, dotR);

        int tx = x + P(24), tw = w - P(24) - P(56);
        if (rowHover && sid != null)
        {
            // Hover buttons, right to left: finish, pin, hand off.
            bool pinned = key == "pinned";
            int bx = x + w - P(6) - P(24), by = y + P(4);
            DrawIconBtn(g, new Rectangle(bx, by, P(24), P(24)), "done", () => ClaudeTray.Mark(sid, "finish"));
            bx -= P(27);
            DrawIconBtn(g, new Rectangle(bx, by, P(24), P(24)), pinned ? "unpin" : "pin", () => ClaudeTray.Mark(sid, pinned ? "unpin" : "pin"));
            bx -= P(27);
            var hb = new Rectangle(bx, by, P(24), P(24));
            DrawIconBtn(g, hb, "handoff", () => ShowHandoffMenu(sid, hb));
        }
        else
        {
            string when;
            if (realKey == "working") when = Ui.Dur(Ui.Now() - ClaudeTray.Num(it["since"]));
            else when = Ui.Ago(ClaudeTray.Num(it["lastActivity"]));
            var ws = TextRenderer.MeasureText(g, when, fTiny, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, when, fTiny, new Point(x + w - P(8) - ws.Width, y + P(9)), Ui.Dim, TextFormatFlags.NoPadding);
            if (key == "pinned") TextRenderer.DrawText(g, "\uE718", fIconSmall, new Point(x + w - P(8) - ws.Width - P(15), y + P(9)), Ui.Accent, TextFormatFlags.NoPadding);
        }

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        int ty = y + P(5);
        TextRenderer.DrawText(g, (string)it["title"], realKey == "unseen" ? fTitleBold : fTitle, new Rectangle(tx, ty, tw, P(18)), Ui.Text, flags);
        ty += P(18);
        if (realKey != "working" && it["detail"] != null)
        {
            TextRenderer.DrawText(g, (string)it["detail"], fSmall, new Rectangle(tx, ty, tw + P(48), P(15)), Ui.Dim, flags);
            ty += P(15);
        }
        var meta = new List<string>();
        if (it["folder"] != null) meta.Add((string)it["folder"]);
        if (it["note"] != null) meta.Add((string)it["note"]);
        string ctxText = null;
        if (it.ContainsKey("ctx") && it["ctx"] != null)
        {
            double c = ClaudeTray.Num(it["ctx"]);
            ctxText = (c >= 1000000 ? (c / 1000000).ToString("0.0") + "M" : Math.Round(c / 1000) + "k") + " context";
        }
        if (!(it["running"] is bool && (bool)it["running"]) && realKey != "cut") meta.Add("closed");
        // Context size first and a little brighter than the rest of the line.
        int mx = tx;
        if (ctxText != null)
        {
            TextRenderer.DrawText(g, ctxText, fTiny, new Point(mx, ty), Ui.Dim, TextFormatFlags.NoPadding);
            mx += TextRenderer.MeasureText(g, ctxText, fTiny, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (meta.Count > 0) { TextRenderer.DrawText(g, " · ", fTiny, new Point(mx, ty), Ui.Faint, TextFormatFlags.NoPadding); mx += TextRenderer.MeasureText(g, " · ", fTiny, Size.Empty, TextFormatFlags.NoPadding).Width; }
        }
        TextRenderer.DrawText(g, string.Join(" · ", meta), fTiny, new Rectangle(mx, ty, Math.Max(0, tx + tw + P(48) - mx), P(14)), Ui.Faint, flags);
        return h;
    }
}
