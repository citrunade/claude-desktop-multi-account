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
        if (args.Length > 1 && args[0] == "--test-send")   // for checking the Enter press: a small test window should receive it
        {
            bool seen = false; string result = "timeout";
            var f = new Form { Text = "Claude Tracker test", Width = 300, Height = 120, StartPosition = FormStartPosition.CenterScreen, KeyPreview = true, TopMost = false };
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) seen = true; };
            f.Shown += (s, e) =>
            {
                var t = new System.Windows.Forms.Timer { Interval = 600 };
                t.Tick += (s2, e2) =>
                {
                    t.Stop();
                    AutoSend.PressEnter(f.Handle, ok =>
                    {
                        var t2 = new System.Windows.Forms.Timer { Interval = 200 };
                        t2.Tick += (s3, e3) => { t2.Stop(); result = "front=" + ok + " enterReceived=" + seen; f.Close(); };
                        t2.Start();
                    });
                };
                t.Start();
            };
            Application.Run(f);
            File.WriteAllText(args[1], result);
            return;
        }
        if (args.Length > 1 && args[0] == "--snapshot")   // for checking the accounts window: draws it to a picture, never shows it
        {
            using (var f = new ManageForm { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false })
            {
                f.Show(); Application.DoEvents();
                using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(args[1]); }
            }
            return;
        }
        if (args.Length > 2 && args[0] == "--render-panel")   // for pictures: draws the panel from saved data (JSON) to a picture
        {
            State = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(args[1]));
            // Sample conversations shown as open in their account's window (and the one in front).
            Func<string, HashSet<string>> ids = key => { var h = new HashSet<string>(); object v; if (State.TryGetValue(key, out v) && v is object[]) foreach (var o in (object[])v) h.Add((string)o); return h; };
            Data.OnScreen = ids("onScreen"); Data.OnScreenFront = ids("onScreenFront");
            panel = new Panel { Location = new Point(-5000, -5000) };
            if (args.Length > 4) panel.Size = new Size(int.Parse(args[3]), int.Parse(args[4]));
            panel.Show(); Application.DoEvents();
            // Off screen nothing paints, and the board only grows to fit while painting: paint it once to a throwaway picture.
            for (int k = 0; k < 2; k++) using (var tmp = new Bitmap(Math.Max(1, panel.Board.Width), Math.Max(1, panel.Board.Height))) { panel.Board.DrawToBitmap(tmp, new Rectangle(0, 0, tmp.Width, tmp.Height)); Application.DoEvents(); }
            using (var b = new Bitmap(panel.Width, panel.Height)) { panel.DrawToBitmap(b, new Rectangle(0, 0, panel.Width, panel.Height)); b.Save(args[2]); }
            if (args.Length > 5) panel.RenderBar(args[5]);   // and the bar on the taskbar
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
            var auto = new System.Windows.Forms.Timer { Interval = 40 };
            auto.Tick += (s, e) => panel.AutoTick();
            auto.Start();
            panel.WatchForeground();
            var tick = new System.Windows.Forms.Timer { Interval = 1000 };
            tick.Tick += (s, e) => { if (panel.Visible) panel.Invalidate(true); };
            tick.Start();

            Fetch();
            ShowPanel();
            // "Open all accounts when Windows starts": only right after sign-in, not when you restart the widget.
            var al = Accounts.Load().autoLaunchAccounts;
            if (al != null && al.Count > 0 && Environment.TickCount >= 0 && Environment.TickCount < 15 * 60 * 1000)
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(3000); Data.AutoLaunchAll(); });
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

    // Room counts for more when the limit resets soon: what's left is spread over the hours left and
    // compared with an even pace over the whole period. With the whole period left, it's just the percent used.
    // No reading counts as half used.
    static double Paced(double used, double reset, double now, double periodHours)
    {
        if (double.IsNaN(used)) return 50;
        if (double.IsNaN(reset)) return used;
        double hoursLeft = Math.Min(periodHours, Math.Max(periodHours / 300, (reset - now) / 3600));
        return Math.Max(0, 100 - Math.Min(100, (100 - used) * periodHours / hoursLeft));
    }

    public static double ResetAt(Dictionary<string, object> acct, string kind)
    {
        if (acct == null) return double.NaN;
        foreach (Dictionary<string, object> w in Arr(acct, "windows"))
            if ((string)w["kind"] == kind && w.ContainsKey("resetsAt")) return Num(w["resetsAt"]);
        return double.NaN;
    }

    public static double Pct(Dictionary<string, object> acct, string kind)
    {
        if (acct == null) return double.NaN;
        foreach (Dictionary<string, object> w in Arr(acct, "windows"))
            if ((string)w["kind"] == kind) return Num(w["percent"]);
        return double.NaN;
    }

    static int lastUnseen = -1;
    static void Apply(Dictionary<string, object> d)
    {
        var cur = Current();
        double five = Pct(cur, "five_hour"), week = Pct(cur, "seven_day");
        int unseen = Arr(d, "unseen").Length, waiting = Arr(d, "waiting").Length;
        if (unseen > lastUnseen && lastUnseen >= 0) panel.FlashNew();   // a new answer came in
        lastUnseen = unseen;
        int working = Arr(d, "working").Length;
        SetIcon(double.IsNaN(five) ? (double?)null : five, unseen > 0);
        string tip = (double.IsNaN(five) ? "5h ?" : "5h " + Math.Round(five) + "%")
                   + (double.IsNaN(week) ? "" : " · wk " + Math.Round(week) + "%")
                   + "\n" + unseen + " new · " + waiting + " seen · " + working + " working";
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

        double now = Num(d["now"]);

        AutoHandoff(d, now);
        KeepClock(d, now);
    }

    // ---------- starting each account's 5-hour clock ----------
    // The 5-hour limit's clock only starts with an account's first message. When an account's window
    // isn't running, send it a one-line message so the clock starts now and resets 5 hours from now,
    // instead of starting whenever you next happen to use it. Sending means opening a new chat in that
    // account and pressing Enter in it, so it only happens while you're away from the keyboard and mouse.
    public const string KeepClockMarker = "[Claude Tracker: 5-hour clock start]";
    const string KeepClockText = "Hi! " + KeepClockMarker + " Reply with just OK.";
    static Dictionary<string, double> clockStarts;
    static bool clockBusy;
    static string ClockFile { get { return Path.Combine(Accounts.TrackerDir, "clock-starts.json"); } }

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    static double IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref li)) return 0;
        return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
    }

    // One line in clock.log each time the reason for (not) starting an account's clock changes.
    static readonly Dictionary<string, string> clockWhy = new Dictionary<string, string>();
    static void ClockNote(string key, string why)
    {
        lock (clockWhy)
        {
            string prev;
            if (clockWhy.TryGetValue(key, out prev) && prev == why) return;
            clockWhy[key] = why;
        }
        try { File.AppendAllText(Path.Combine(Accounts.TrackerDir, "clock.log"), DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + key + ": " + why + Environment.NewLine); } catch { }
    }

    static void KeepClock(Dictionary<string, object> d, double now)
    {
        var cs = Accounts.Load();
        if (!cs.keepClock) { ClockNote("all", "setting is off"); return; }
        ClockNote("all", cs.keepClockAnytime ? "on, even while you're using the PC" : "on, only while you're away");
        if (clockBusy) return;
        bool away = cs.keepClockAnytime || IdleSeconds() >= 120;
        if (clockStarts == null)
        {
            clockStarts = new Dictionary<string, double>();
            var saved = Data.ReadJson(ClockFile);
            if (saved != null) foreach (var kv in saved) { var v = Data.Dbl(kv.Value); if (v != null) clockStarts[kv.Key] = v.Value; }
        }
        foreach (Dictionary<string, object> a in Arr(d, "accounts"))
        {
            int n = Convert.ToInt32(a["n"]);
            if (n >= 99) continue;
            string key = "account " + n;
            if (!(a["canStart"] is bool && (bool)a["canStart"])) { ClockNote(key, "can't open a chat in it"); continue; }
            double reset = ResetAt(a, "five_hour"), pct = Pct(a, "five_hour");
            double asOf = a["asOf"] == null ? double.NaN : Num(a["asOf"]);
            if (!double.IsNaN(reset) && reset > now) { ClockNote(key, "window running"); continue; }
            if (double.IsNaN(reset) && !double.IsNaN(pct) && pct > 0 && now - asOf < 5 * 3600) { ClockNote(key, "used lately (" + Math.Round(pct) + "%), reset time unknown"); continue; }
            double last; clockStarts.TryGetValue(n.ToString(), out last);
            if (now - last < 5 * 3600) { ClockNote(key, "started or tried under 5 hours ago"); continue; }
            if (!away) { ClockNote(key, "window not running; waiting for 2 minutes without mouse or keyboard"); continue; }
            ClockNote(key, "starting its clock");
            clockStarts[n.ToString()] = now;
            try { File.WriteAllText(ClockFile, new JavaScriptSerializer().Serialize(clockStarts)); } catch { }
            clockBusy = true;
            string name = (string)a["name"]; int nn = n;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool opened = false;
                try { opened = Data.StartConversation(nn, KeepClockText); } catch { }
                if (!opened) { clockBusy = false; ClockNote("account " + nn, "couldn't open a new chat"); return; }
                AutoSend.Run(nn, panel, sent =>
                {
                    clockBusy = false;
                    ClockNote("account " + nn, sent ? "sent" : "not sent: its window wasn't in front; retrying in 15 minutes");
                    // Not sent (its window wasn't in front): try again in 15 minutes rather than in 5 hours.
                    if (!sent) clockStarts[nn.ToString()] = Data.Now() - 5 * 3600 + 15 * 60;
                    else tray.ShowBalloonTip(4000, "Started " + name + "'s 5-hour clock", "Sent a one-line message so its 5-hour limit resets 5 hours from now.", ToolTipIcon.Info);
                });
            });
            return;   // one account per refresh
        }
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
            foreach (var cat in new[] { "unseen", "waiting", "pinned" })
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
                    if (mode == "send")
                    {
                        Handoff(sid, n, true, toName);
                        tray.ShowBalloonTip(6000, "Moving to " + toName, "\"" + title + "\" hit " + fromName + "'s limit. Continuing it in a new chat on " + toName + "; your mouse and keys pause for a moment while it sends.", ToolTipIcon.Info);
                    }
                    else if (mode == "auto")
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
            double f = Paced(five, ResetAt(a, "five_hour"), now, 5);
            double w = Paced(week, ResetAt(a, "seven_day"), now, 168);
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
    public static void LaunchAccount(int n) { Act(() => Data.LaunchAccount(n), "Couldn't open that account."); }
    public static void OpenConversation(string sid) { Act(() => Data.OpenConversation(sid), "Couldn't find that conversation in Claude."); }
    public static void Mark(string sid, string action) { Act(() => Marks.Set(sid, action), null); }
    public static void Handoff(string sid, int n, bool send = false, string doneTitle = null)
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
        // A chat with no project folder runs in a temporary folder made just for it (deleted with it).
        // Carrying that over makes Claude ask "Trust this workspace?" for a folder nobody chose, so the
        // new chat gets its own temporary folder instead.
        string folder = Data.IsTempWorkspace(cwd) ? null : cwd;
        // Never press Enter for you while Claude may be asking whether to trust the folder: that answer is yours.
        bool askTrust = folder != null && !Data.IsTrustedFolder(folder);
        if (askTrust) send = false;
        Act(() =>
        {
            bool ok = Data.StartConversation(n, text, folder);
            if (ok) Data.MarkHandedOff(sid);
            if (ok && askTrust)
                try { panel.BeginInvoke((Action)(() => tray.ShowBalloonTip(8000, "Check the new chat", "Claude is asking whether to trust " + folder + ". Answer it, then press Enter to send the hand-off.", ToolTipIcon.Info))); } catch { }
            if (ok && send)
                AutoSend.Run(n, panel, sent =>
                {
                    if (!sent) tray.ShowBalloonTip(8000, "Press Enter to continue", "The new chat on " + doneTitle + " is ready, but its window wasn't in front, so it wasn't sent.", ToolTipIcon.Info);
                });
            return ok;
        }, "Couldn't open that account.");
    }

    static ManageForm manager;
    public static void OpenManager()
    {
        if (manager == null || manager.IsDisposed)
        {
            manager = new ManageForm();
            manager.FormClosed += (s, e) => Fetch();
        }
        // Open in the middle of the screen the panel is on, above it.
        var wa = Screen.FromControl(panel != null ? (Control)panel : manager).WorkingArea;
        manager.StartPosition = FormStartPosition.Manual;
        if (manager.WindowState == FormWindowState.Minimized) manager.WindowState = FormWindowState.Normal;
        manager.Location = new Point(wa.Left + Math.Max(0, (wa.Width - manager.Width) / 2), wa.Top + Math.Max(0, (wa.Height - manager.Height) / 2));
        manager.TopMost = true;
        manager.Show(); manager.BringToFront(); manager.Activate();
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
    readonly Control header, footer;
    Rectangle btnModeClaude, btnModeElse;
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
        AllowTransparency = true; Opacity = 0.999;   // fadeable from the start (see Rest)
        Text = "Claude Tracker";
        expandedSize = new Size(P(380), P(620));
        Size = expandedSize;
        Padding = new Padding(P(4));   // a thin edge to grab for resizing

        header = new DoubleBufferedControl { Dock = DockStyle.Top, Height = P(40), BackColor = Ui.Bg };
        header.Paint += PaintHeader;
        header.MouseDown += HeaderDown;
        header.MouseMove += (s, e) => { string hb = btnCollapse.Contains(e.Location) ? "c" : btnClose.Contains(e.Location) ? "x" : btnGear.Contains(e.Location) ? "g" : null; if (hb != hoverBtn) { hoverBtn = hb; header.Invalidate(); } header.Cursor = hb != null ? Cursors.Hand : Cursors.Default; };
        header.MouseLeave += (s, e) => { hoverBtn = null; header.Invalidate(); };

        host = new ScrollHost { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        Board = new Board(this) { Location = new Point(0, 0) };
        host.Controls.Add(Board);
        host.Resize += (s, e) => { if (!anim.Enabled) Board.Width = host.ClientSize.Width; };   // no re-wrapping mid-animation
        Board.SizeChanged += (s, e) => host.Clamp();
        Board.MouseWheel += (s, e) => host.ScrollBy(e.Delta / 2);
        Board.Width = expandedSize.Width - P(20);

        // The bottom bar: how the panel behaves in a Claude window and elsewhere, one click to switch each.
        footer = new DoubleBufferedControl { Dock = DockStyle.Bottom, Height = P(24), BackColor = Ui.Bg };
        footer.Paint += PaintFooter;
        footer.MouseMove += (s, e) => { string hb = btnModeClaude.Contains(e.Location) ? "mc" : btnModeElse.Contains(e.Location) ? "me" : null; if (hb != hoverBtn) { hoverBtn = hb; footer.Invalidate(); footerTip.SetToolTip(footer, hb == "mc" ? ModeTip(true) : hb == "me" ? ModeTip(false) : null); } footer.Cursor = hb != null ? Cursors.Hand : Cursors.Default; };
        footer.MouseLeave += (s, e) => { if (hoverBtn == "mc" || hoverBtn == "me") { hoverBtn = null; footer.Invalidate(); } };
        footer.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (btnModeClaude.Contains(e.Location)) SwitchMode(true);
            else if (btnModeElse.Contains(e.Location)) SwitchMode(false);
        };

        Controls.Add(host);
        Controls.Add(header);
        Controls.Add(footer);
        LoadPlace();
        openSpot = Bounds;
        bar = new BarWindow(this);
        VisibleChanged += (s, e) => { if (Visible) { bar.Bounds = PillRect(); bar.Show(); } else bar.Hide(); };
        Invalidated += (s, e) => bar.Invalidate();
        header.Invalidated += (s, e) => bar.Invalidate();
    }
    public BarWindow bar;
    Rectangle openSpot;   // where the panel sits open in "stays open" mode (you can put it anywhere)

    public int P(float v) { return (int)Math.Round(v * S); }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: no taskbar button
            cp.ExStyle |= 0x02000000;  // WS_EX_COMPOSITED: the whole window paints as one picture, so resizing doesn't flicker
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
        using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, P(14), h / 2 - P(4), P(8), P(8));
        using (var f = new Font("Segoe UI Semibold", 10.5f))
            TextRenderer.DrawText(g, "Claude Code", f, new Point(P(28), h / 2 - P(10)), Ui.Text);
        btnClose = new Rectangle(w - P(36), h / 2 - P(13), P(26), P(26));
        // In hover mode there is nothing to shrink by hand: the – button only shows in "stays open" mode.
        btnCollapse = hoverMode ? Rectangle.Empty : new Rectangle(w - P(64), h / 2 - P(13), P(26), P(26));
        btnGear = new Rectangle(w - (hoverMode ? P(64) : P(92)), h / 2 - P(13), P(26), P(26));
        if (!hoverMode) DrawHeaderBtn(g, btnCollapse, "c");
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

    DateTime flashUntil = DateTime.MinValue;
    public void FlashNew() { flashUntil = DateTime.UtcNow.AddSeconds(4); }

    // For pictures: the bar on the taskbar, drawn to a file.
    public void RenderBar(string path)
    {
        var sz = PillSize;
        using (var b = new Bitmap(sz.Width, sz.Height))
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(Ui.Bg);
            PaintBar(g, sz.Width, sz.Height);
            using (var pen = new Pen(Ui.Line)) g.DrawRectangle(pen, 0, 0, sz.Width - 1, sz.Height - 1);
            b.Save(path);
        }
    }
    // Draws the bar on the taskbar (its own window, see BarWindow).
    public void PaintBar(Graphics g, int w, int h)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        PaintPill(g, w, h);
    }
    void PaintPill(Graphics g, int w, int h)
    {
        var st = ClaudeTray.State;
        var cur = ClaudeTray.Current();
        double five = ClaudeTray.Pct(cur, "five_hour");
        int unseen = ClaudeTray.Arr(st, "unseen").Length, working = ClaudeTray.Arr(st, "working").Length;
        // A new answer: the bar glows amber, pulsing for a few seconds, then keeps a faint glow while any are unseen.
        double flash = (flashUntil - DateTime.UtcNow).TotalMilliseconds;
        int glow = flash > 0 ? (int)(60 + 70 * Math.Abs(Math.Sin(flash / 1000.0 * Math.PI * 1.5))) : unseen > 0 ? 28 : 0;
        if (glow > 0) using (var b = new SolidBrush(Color.FromArgb(glow, Ui.Warn))) g.FillRectangle(b, 0, 0, w, h);
        int x = P(12);
        using (var b = new SolidBrush(Ui.LimitColor(double.IsNaN(five) ? (double?)null : five)))
        using (var p = Ui.Rounded(new RectangleF(x, h / 2 - P(10), P(34), P(20)), P(10))) g.FillPath(b, p);
        using (var f = new Font("Segoe UI Semibold", 8.5f))
            TextRenderer.DrawText(g, double.IsNaN(five) ? "?" : Math.Round(five) + "%", f, new Rectangle(x, h / 2 - P(10), P(34), P(20)), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        x += P(42);
        var parts = new List<KeyValuePair<string, Color>>();
        if (unseen > 0) parts.Add(new KeyValuePair<string, Color>(unseen + " new", Ui.Warn));
        if (working > 0) parts.Add(new KeyValuePair<string, Color>(working + " busy", Ui.Blue));
        if (parts.Count == 0) parts.Add(new KeyValuePair<string, Color>("all quiet", Ui.Dim));
        using (var f = new Font("Segoe UI", 9f))
            foreach (var kv in parts)
            {
                var sz = TextRenderer.MeasureText(g, kv.Key, f, Size.Empty, TextFormatFlags.NoPadding);
                if (x + sz.Width > w - P(40)) break;   // the narrow bar shows what fits
                TextRenderer.DrawText(g, kv.Key, f, new Point(x, h / 2 - sz.Height / 2), kv.Value, TextFormatFlags.NoPadding);
                x += sz.Width + P(10);
            }
    }

    readonly ToolTip footerTip = new ToolTip();
    string ModeTip(bool claude)
    {
        bool open = (claude ? set.panelInClaude : set.panelElsewhere) == "open";
        return (claude ? "While a Claude window is in front: " : "While anything else is in front: ") + (open ? "stays open" : "opens when the mouse is over it") + " (click to switch)";
    }

    // The bottom bar: two quiet switches, a Claude window and the rest of the PC. On = stays open, off = opens on hover.
    void PaintFooter(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int w = footer.Width, h = footer.Height;
        using (var pen = new Pen(Color.FromArgb(36, 36, 34))) g.DrawLine(pen, P(10), 0, w - P(10), 0);
        int gw = P(74), cy = h / 2;
        btnModeClaude = new Rectangle(P(10), 0, gw, h);
        btnModeElse = new Rectangle(P(10) + gw + P(6), 0, gw, h);
        DrawModeSwitch(g, btnModeClaude, true, set.panelInClaude == "open", ctxClaude, hoverBtn == "mc");
        DrawModeSwitch(g, btnModeElse, false, set.panelElsewhere == "open", !ctxClaude, hoverBtn == "me");
    }

    static readonly Font fModeTiny = new Font("Segoe UI", 7f);
    void DrawModeSwitch(Graphics g, Rectangle r, bool claude, bool on, bool current, bool hot)
    {
        Color ink = hot ? Ui.Dim : current ? Color.FromArgb(128, 125, 118) : Color.FromArgb(88, 86, 80);
        int cy = r.Y + r.Height / 2, x = r.X;
        using (var pen = new Pen(ink, 1f * S))
        {
            if (claude)
            {
                // a small window with Claude's orange spark filling it
                var win = new Rectangle(x, cy - P(7), P(16), P(14));
                g.DrawRectangle(pen, win);
                g.DrawLine(pen, win.X, win.Y + P(2.5f), win.Right, win.Y + P(2.5f));
                float sx = win.X + win.Width / 2f + 0.5f, sy = win.Y + P(8.3f), k = P(4f);
                int alpha = hot ? 255 : current ? 220 : 120;
                using (var sp = new Pen(Color.FromArgb(alpha, Ui.Accent), 1.3f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 8; i++)
                    {
                        // eight rays, long and short in turn: Claude's spark, readable at this size
                        double a = Math.PI * 2 * i / 8 + 0.2;
                        float len = k * (i % 2 == 0 ? 1.05f : 0.7f), r0 = k * 0.12f;
                        g.DrawLine(sp, sx + r0 * (float)Math.Cos(a), sy + r0 * (float)Math.Sin(a), sx + len * (float)Math.Cos(a), sy + len * (float)Math.Sin(a));
                    }
            }
            else
            {
                // a monitor on a stand
                var mon = new Rectangle(x, cy - P(6), P(15), P(9));
                g.DrawRectangle(pen, mon);
                g.DrawLine(pen, x + P(7.5f), mon.Bottom, x + P(7.5f), mon.Bottom + P(2));
                g.DrawLine(pen, x + P(4.5f), mon.Bottom + P(2.5f), x + P(10.5f), mon.Bottom + P(2.5f));
            }
        }
        // the switch
        // a square switch
        var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
        var track = new Rectangle(x + P(21), cy - P(4), P(18), P(8));
        using (var b = new SolidBrush(on ? Color.FromArgb(70, 68, 63) : Color.FromArgb(34, 34, 32))) g.FillRectangle(b, track);
        using (var pen = new Pen(Color.FromArgb(56, 55, 51))) g.DrawRectangle(pen, track);
        int kd = P(6), kx = on ? track.Right - kd - P(1) : track.X + P(1) + 1;
        using (var b = new SolidBrush(on ? (hot ? Ui.Text : Color.FromArgb(190, 188, 180)) : ink)) g.FillRectangle(b, kx, cy - kd / 2 + 1, kd, kd - 1);
        g.SmoothingMode = sm;
        TextRenderer.DrawText(g, on ? "open" : "hover", fModeTiny, new Point(x + P(43), cy - P(6)), ink, TextFormatFlags.NoPadding);
    }

    // Flips one context between "hover" and "stays open" (open, not shrunk), and saves it.
    void SwitchMode(bool claude)
    {
        var s = Accounts.Load();
        if (claude) { s.panelInClaude = s.panelInClaude == "open" ? "hover" : "open"; s.panelInClaudeShrunk = false; }
        else { s.panelElsewhere = s.panelElsewhere == "open" ? "hover" : "open"; s.panelElsewhereShrunk = false; }
        Accounts.Save(s);
        set = s; settingsAt = DateTime.UtcNow;
        footer.Invalidate();
    }

    void HeaderDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (btnCollapse.Contains(e.Location)) { ClickToggle(!targetCollapsed); return; }
        if (btnClose.Contains(e.Location)) { SavePlace(); Hide(); return; }
        if (btnGear.Contains(e.Location)) { ClaudeTray.OpenManager(); return; }
        var start = Location;
        dragging = true;
        try
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // drag like a title bar (returns when the mouse is let go)
        }
        finally { dragging = false; }
        if (Location == start)
        {
            // A plain click on the shrunk bar reopens it (in "stays open" mode).
            if (!hoverMode && targetCollapsed) ClickToggle(false);
            else if (hoverMode && targetCollapsed) { hoverAt = DateTime.MinValue; AnimateTo(false); }
            return;
        }
        Snap();
        SavePlace();
    }

    // ---------- open / closed ----------
    // Settings say whether the panel is open or closed while a Claude window is in front and while
    // anything else is. When closed, the bar opens either while the mouse is on it (hover) or by a
    // click; a click (on the bar, or on the – button) changes the setting for where you are, so it sticks.
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static int MenusOpen;                 // a menu from the panel is showing: stay open
    bool dragging, ctxClaude, hoverMode, targetCollapsed, footerCtx;
    DateTime hoverAt = DateTime.MinValue, hoverLeft = DateTime.MinValue, settingsAt = DateTime.MinValue, leftClaudeAt = DateTime.MinValue;
    AccountSettings set = new AccountSettings();
    uint fgPid; bool fgIsClaude; string fgName = "?", lastLogLine;
    static readonly int MyPid = Process.GetCurrentProcess().Id;
    Size PillSize { get { return new Size(P(216), P(40)); } }

    // ---------- the shrunk bar lives on the taskbar ----------
    // Where the taskbar is on the main screen (bottom or top). If it's hidden or on a side, a strip
    // along the bottom of the screen instead.
    Rectangle TaskbarStrip()
    {
        var sc = Screen.PrimaryScreen; var b = sc.Bounds; var wa = sc.WorkingArea;
        // Ask the taskbar itself where it is first: after a game changes the resolution, the screen's
        // remembered work area can be out of date and the bar would land off the taskbar.
        var tw = FindWindow("Shell_TrayWnd", null); RECT tr;
        if (tw != IntPtr.Zero && IsWindowVisible(tw) && GetWindowRect(tw, out tr))
        {
            var t = new Rectangle(tr.L, tr.T, tr.R - tr.L, tr.B - tr.T);
            if (t.Width > t.Height && t.Height >= P(20) && t.Height < b.Height / 4 && b.Contains(t)) return t;
        }
        if (wa.Bottom < b.Bottom) return new Rectangle(b.Left, wa.Bottom, b.Width, b.Bottom - wa.Bottom);
        if (wa.Top > b.Top) return new Rectangle(b.Left, b.Top, b.Width, wa.Top - b.Top);
        return new Rectangle(b.Left, b.Bottom - PillSize.Height - P(4), b.Width, PillSize.Height + P(4));
    }
    readonly string pillFile = Path.Combine(ClaudeTray.Dir, "pill.txt");
    int pillX = int.MinValue;   // its left edge, from the screen's left; you move it by dragging along the taskbar
    Rectangle PillRect()
    {
        var tb = TaskbarStrip(); var sz = PillSize;
        if (pillX == int.MinValue) { try { pillX = int.Parse(File.ReadAllText(pillFile).Trim()); } catch { pillX = P(8); } }
        int x = Math.Max(tb.Left, Math.Min(tb.Left + pillX, tb.Right - sz.Width));
        int h = Math.Min(sz.Height, tb.Height);
        return new Rectangle(x, tb.Top + (tb.Height - h) / 2, sz.Width, h);
    }
    void SavePill(int left)
    {
        pillX = left - TaskbarStrip().Left;
        try { File.WriteAllText(pillFile, pillX.ToString()); } catch { }
    }
    // The taskbar comes to the front when you click it, so put the bar back on top of it now and then
    // (not over a full-screen app).
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    int pillTicks; IntPtr pillFg; bool hiddenForFullScreen, hideOverGames;
    // A game or video filling the whole main screen (not the desktop itself).
    bool FullScreenInFront(IntPtr fg)
    {
        RECT fr; var sb = Screen.PrimaryScreen.Bounds;
        if (fg == IntPtr.Zero || fg == Handle || (bar != null && fg == bar.Handle)) return false;
        if (!GetWindowRect(fg, out fr) || fr.L > sb.Left || fr.T > sb.Top || fr.R < sb.Right || fr.B < sb.Bottom) return false;
        var sb2 = new System.Text.StringBuilder(64); GetClassName(fg, sb2, 64);
        var cls = sb2.ToString();
        return cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd";
    }
    void KeepPillOnTaskbar()
    {
        if (bar == null || dragging) return;
        var fg = GetForegroundWindow();
        // While a full-screen game is in front, take the bar off the screen entirely so it never
        // fights the game (or Windows) over the top spot; bring it back once you leave the game.
        bool full = FullScreenInFront(fg);
        if (full)
        {
            // Either hide it (setting), or leave it exactly where it is: no moving or re-raising
            // during the game, so it never fights the game or Windows over the top spot.
            if (hideOverGames && bar.Visible) { bar.Hide(); hiddenForFullScreen = true; }
            return;
        }
        if (hiddenForFullScreen)
        {
            hiddenForFullScreen = false;
            if (Visible) { bar.Bounds = PillRect(); bar.Show(); }
        }
        if (!bar.Visible) return;
        // Clicking the taskbar (to switch Claude windows) puts it over the bar: lift the bar back the
        // moment the front window changes or the taskbar is seen in front of it (checked every 40 ms),
        // not up to a third of a second later, so it doesn't blink.
        bool changed = fg != pillFg; pillFg = fg;
        if (++pillTicks % 8 != 0 && !changed && !bar.TaskbarInFront()) return;
        var pr = PillRect();
        if (bar.Bounds != pr) bar.Bounds = pr;
        SetWindowPos(bar.Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);   // topmost, no move, no size, no focus
    }

    // The bar was pressed: drag it along the taskbar, or a plain click opens / shrinks the panel.
    DateTime lastBarClick = DateTime.MinValue;
    public void BarClicked()
    {
        // A double-click is two presses: the second must not shrink what the first just opened.
        var now = DateTime.UtcNow;
        bool quickSecond = (now - lastBarClick).TotalMilliseconds < SystemInformation.DoubleClickTime;
        lastBarClick = now;
        if (quickSecond) return;
        if (!hoverMode) ClickToggle(!targetCollapsed);
        else if (targetCollapsed) { hoverAt = DateTime.MinValue; AnimateTo(false); }
    }
    // While the bar is dragged: it stays on the taskbar, and an open panel above it comes along.
    public void BarDragTo(int left)
    {
        dragging = true;
        var tb = TaskbarStrip(); var pr = PillRect();
        int l = Math.Max(tb.Left, Math.Min(left, tb.Right - pr.Width));
        bar.Bounds = new Rectangle(l, pr.Y, pr.Width, pr.Height);
        if (!collapsed && hoverMode && !anim.Enabled) Location = OpenRect(bar.Bounds).Location;
    }
    public void BarDropped()
    {
        dragging = false;
        SavePill(bar.Left);
        bar.Bounds = PillRect();
        if (!collapsed && hoverMode) { Location = OpenRect().Location; restAt = Location; }
    }

    // Where the panel sits when open: above the bar in hover mode, or where you put it in "stays open".
    Rectangle OpenRect() { return OpenRect(bar != null ? PillRect() : Bounds); }
    Rectangle OpenRect(Rectangle pill)
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        var size = expandedSize;
        if (!hoverMode && openSpot.Width > 0)
        {
            var r = new Rectangle(openSpot.Location, size);
            r.X = Math.Max(wa.Left, Math.Min(r.X, wa.Right - r.Width));
            r.Y = Math.Max(wa.Top, Math.Min(r.Y, wa.Bottom - r.Height));
            return r;
        }
        bool right = pill.Left + pill.Width / 2 > wa.Left + wa.Width / 2;
        bool top = TaskbarStrip().Top < wa.Top + wa.Height / 2;
        var o = new Rectangle(right ? pill.Right - size.Width : pill.Left, top ? wa.Top : wa.Bottom - size.Height, size.Width, size.Height);
        o.X = Math.Max(wa.Left, Math.Min(o.X, wa.Right - o.Width));
        o.Y = Math.Max(wa.Top, Math.Min(o.Y, wa.Bottom - o.Height));
        return o;
    }

    bool InFront(out bool mine)
    {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        mine = pid == MyPid;
        if (pid != fgPid)
        {
            fgPid = pid; fgIsClaude = false; fgName = "?";
            try { using (var p = Process.GetProcessById((int)pid)) { fgName = p.ProcessName; fgIsClaude = string.Equals(p.ProcessName, "claude", StringComparison.OrdinalIgnoreCase); } } catch { }
        }
        return fgIsClaude;
    }

    AccountSettings Settings()
    {
        if ((DateTime.UtcNow - settingsAt).TotalSeconds > 1) { set = Accounts.Load(); settingsAt = DateTime.UtcNow; if (footer != null) footer.Invalidate(); }
        return set;
    }
    // Older settings said "closed" for what is now hover mode.
    bool HoverModeFor(AccountSettings s) { return (ctxClaude ? s.panelInClaude : s.panelElsewhere) != "open"; }
    bool ShrunkFor(AccountSettings s) { return ctxClaude ? s.panelInClaudeShrunk : s.panelElsewhereShrunk; }

    // Runs every 40 ms: cheap (front window and mouse position; the process is looked up only when the front window changes).
    public void AutoTick()
    {
        // Safety net: "dragging" with the mouse button up means the end of a drag was missed. Don't stay frozen.
        if (dragging && (Control.MouseButtons & MouseButtons.Left) == 0) dragging = false;
        if (!Visible || dragging) return;
        var s = Settings();
        bool mine;
        bool onClaude = InFront(out mine);
        // While you use the panel itself, keep the rule of where you came from. Coming to Claude counts at
        // once; leaving it counts only after 0.15 s, because switching between two Claude windows passes
        // through the taskbar for a split second.
        // Switching windows takes effect at once. The taskbar itself doesn't count: clicking it to go
        // from one Claude window to another passes through it for a split second.
        bool switched = false;
        if (!mine && !OnTaskbar() && onClaude != ctxClaude) { ctxClaude = onClaude; switched = true; }
        hideOverGames = s.hideOverGames;
        bool hm = HoverModeFor(s);
        if (hm != hoverMode) { hoverMode = hm; header.Invalidate(); }
        if (ctxClaude != footerCtx) { footerCtx = ctxClaude; footer.Invalidate(); }
        // Judge the mouse against where the panel is heading, not the in-between frame of the animation,
        // with a small margin, so it doesn't flicker open/closed while growing or at the very edge.
        // While shrinking, the mouse coming back onto what is still showing counts too, so it reopens
        // instead of finishing the close first.
        var zone = collapsed ? Rectangle.Empty : anim.Enabled && !targetCollapsed ? animTo : Bounds;
        if (!zone.IsEmpty) zone.Inflate(P(6), P(6));
        // It grows up from the taskbar, so the bar's spot there counts too (on its own, not everything in between).
        var pillZone = PillRect(); pillZone.Inflate(P(6), P(6));
        var mp = Control.MousePosition;
        bool hover = zone.Contains(mp) || (hoverMode && pillZone.Contains(mp)) || MenusOpen > 0;
        if (!hover) hoverAt = DateTime.MinValue;
        bool open;
        if (hoverMode)
        {
            // A small bar; open only while the mouse is over it (and a moment after it leaves).
            open = false;
            if (hover)
            {
                hoverLeft = DateTime.MinValue;
                // Shrunk: open once the mouse rests on the bar for a moment, so passing over the taskbar
                // doesn't pop it and you can grab the bar to slide it first.
                if (!targetCollapsed) open = true;
                else
                {
                    if (hoverAt == DateTime.MinValue) hoverAt = DateTime.UtcNow;
                    open = (DateTime.UtcNow - hoverAt).TotalMilliseconds >= 220;
                }
            }
            // Just switched windows and the mouse isn't on it: close now, no lingering.
            else if (!targetCollapsed && switched) open = false;
            else if (!targetCollapsed && hoverLeft == DateTime.MinValue) { hoverLeft = DateTime.UtcNow; open = true; }
            else if (!targetCollapsed && (DateTime.UtcNow - hoverLeft).TotalMilliseconds < 450) open = true;
        }
        else open = !ShrunkFor(s);   // stays open over other windows unless you shrank it here
        if (open == targetCollapsed)
        {
            // Switching between open windows: appear or disappear at once. When a Claude window is being
            // minimised or brought back (Windows animates it), fade along with it. The mouse always fades.
            bool withClaude = DateTime.UtcNow < claudeAnimUntil;
            if (switched && !withClaude) SwitchNow(!open);
            else AnimateTo(!open);
        }
        KeepPillOnTaskbar();
        Log("front=" + fgName + " claude=" + ctxClaude + " hover=" + hover + " open=" + open);
        if (DateTime.UtcNow < flashUntil.AddMilliseconds(100) && bar != null) bar.Invalidate();   // animate the glow
    }

    void Log(string line)
    {
        if (line == lastLogLine) return;
        lastLogLine = line;
        try
        {
            var log = Path.Combine(ClaudeTray.Dir, "auto.log");
            if (File.Exists(log) && new FileInfo(log).Length > 200000) File.Delete(log);
            File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss.ff") + " " + line + Environment.NewLine);
        }
        catch { }
    }

    // In "stays open" mode: the – button shrinks it, a click on the bar reopens it; remembered for that place.
    void ClickToggle(bool collapse)
    {
        var s = Accounts.Load();
        if (ctxClaude) s.panelInClaudeShrunk = collapse; else s.panelElsewhereShrunk = collapse;
        Accounts.Save(s);
        set = s; settingsAt = DateTime.UtcNow;
        AnimateTo(collapse);
    }

    // ---------- animation ----------
    // Grows from (and shrinks to) the corner nearest the screen's corner, so a panel stuck to an edge stays stuck.
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 5 };
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    bool fineTimer;
    Rectangle animFrom, animTo; DateTime animStart; bool animHooked;
    double AnimMs { get { return targetCollapsed ? 170 : 230; } }

    // No animation: show or hide the panel at once. It stays fully drawn while hidden, so showing it
    // is immediate (nothing to lay out or paint).
    void SwitchNow(bool c)
    {
        bool wasAnim = anim.Enabled;
        anim.Stop();
        if (fineTimer) { timeEndPeriod(1); fineTimer = false; }
        hoverAt = DateTime.MinValue; hoverLeft = DateTime.MinValue;
        targetCollapsed = c;
        Opacity = Rest;
        if (c)
        {
            if (wasAnim) Location = restAt;
            collapsed = true;
            Reveal(false);
        }
        else
        {
            var to = OpenRect();
            if (Bounds != to) Bounds = to;
            restAt = to.Location;
            collapsed = false;
            Reveal(true);
        }
    }

    // Hidden: cloaked (Windows keeps its picture but shows nothing) and clicks pass through it.
    void Reveal(bool show)
    {
        Cloak(!show);
        if (!IsHandleCreated) return;
        int ex = GetWindowLong(Handle, -20);
        int want = show ? ex & ~0x20 : ex | 0x20;   // WS_EX_TRANSPARENT
        if (want != ex) SetWindowLong(Handle, -20, want);
    }

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    void Cloak(bool on)
    {
        if (!IsHandleCreated) return;
        int v = on ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref v, 4); } catch { }
    }

    // The taskbar (or its overflow / Start) is in front.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    static bool OnTaskbar()
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(GetForegroundWindow(), sb, 64);
        string c = sb.ToString();
        return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland" || c == "Windows.UI.Core.CoreWindow";
    }

    // React the moment a different window comes to the front, not on the next check.
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    WinEventProc fgProc, minProc; IntPtr fgHook, minHook;
    DateTime claudeAnimUntil = DateTime.MinValue;   // a Claude window is minimising or coming back right now
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    public void WatchForeground()
    {
        if (fgHook != IntPtr.Zero) return;
        fgProc = (hk, ev, h, obj, child, th, tm) =>
        {
            try
            {
                // Brought back from the taskbar: it can still be minimised the moment it comes to the front.
                if (obj == 0 && IsIconic(h) && IsClaude(h)) claudeAnimUntil = DateTime.UtcNow.AddMilliseconds(700);
                AutoTick();
            }
            catch { }
        };
        fgHook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, fgProc, 0, 0, 0);
        minProc = (hk, ev, h, obj, child, th, tm) =>
        {
            try
            {
                if (obj != 0 || !IsClaude(h)) return;
                claudeAnimUntil = DateTime.UtcNow.AddMilliseconds(700);
                AutoTick();
            }
            catch { }
        };
        minHook = SetWinEventHook(0x16 /* MINIMIZESTART */, 0x17 /* MINIMIZEEND */, IntPtr.Zero, minProc, 0, 0, 0);
    }
    static bool IsClaude(IntPtr h)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        try { using (var p = Process.GetProcessById((int)pid)) return string.Equals(p.ProcessName, "claude", StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    void AnimateTo(bool c)
    {
        if (c && collapsed) { targetCollapsed = true; return; }
        targetCollapsed = c;
        int rise = P(22);
        animStart = DateTime.UtcNow;
        if (!c)
        {
            var to = OpenRect();
            if (collapsed)
            {
                // From hidden: at full size, invisible, a little lower; then fade in and rise.
                Opacity = 0;
                Bounds = new Rectangle(to.X, to.Y + rise, to.Width, to.Height);
                collapsed = false;
                Reveal(true);
            }
            posTo = to.Location; fadeTo = Rest; restAt = to.Location;
        }
        else
        {
            if (!anim.Enabled) restAt = Location;
            posTo = new Point(restAt.X, restAt.Y + rise); fadeTo = 0;
        }
        animFrom = Bounds; animTo = new Rectangle(posTo, Size);
        posFrom = Location; fadeFrom = Opacity;
        if (!animHooked) { anim.Tick += AnimStep; animHooked = true; }
        if (!fineTimer) { timeBeginPeriod(1); fineTimer = true; }   // frames every few ms instead of every 16
        anim.Start();
    }

    const double Rest = 0.999;   // just under fully solid, so the window stays fadeable and never flashes switching modes
    Point posFrom, posTo, restAt; double fadeFrom, fadeTo;

    void AnimStep(object sender, EventArgs e)
    {
        double t = Math.Min(1, (DateTime.UtcNow - animStart).TotalMilliseconds / AnimMs);
        // Opening: quick start, long soft landing. Closing: eases in and out.
        double k = targetCollapsed ? (t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2) : 1 - Math.Pow(1 - t, 5);
        Func<int, int, int> mix = (a, b) => (int)Math.Round(a + (b - a) * k);
        Location = new Point(mix(posFrom.X, posTo.X), mix(posFrom.Y, posTo.Y));
        Opacity = fadeFrom + (fadeTo - fadeFrom) * k;
        if (t < 1) return;
        anim.Stop();
        if (fineTimer) { timeEndPeriod(1); fineTimer = false; }
        if (targetCollapsed)
        {
            // Faded away: hide it, ready to come straight back at its spot.
            collapsed = true;
            Reveal(false);
            Location = restAt;
            Opacity = Rest;
        }
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    void Composited(bool on)
    {
        if (!IsHandleCreated) return;
        const int GWL_EXSTYLE = -20, WS_EX_COMPOSITED = 0x02000000;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        int want = on ? ex | WS_EX_COMPOSITED : ex & ~WS_EX_COMPOSITED;
        if (want != ex) SetWindowLong(Handle, GWL_EXSTYLE, want);
    }

    // ---------- sticking to the taskbar and the screen's sides ----------
    Rectangle SnapRect(Rectangle r)
    {
        var wa = Screen.FromRectangle(r).WorkingArea;
        int near = P(40), x = r.X, y = r.Y;
        if (Math.Abs(r.Left - wa.Left) < near) x = wa.Left;
        else if (Math.Abs(wa.Right - r.Right) < near) x = wa.Right - r.Width;
        if (Math.Abs(r.Top - wa.Top) < near) y = wa.Top;
        else if (Math.Abs(wa.Bottom - r.Bottom) < near) y = wa.Bottom - r.Height;   // right on top of the taskbar
        return new Rectangle(x, y, r.Width, r.Height);
    }
    void Snap()
    {
        if (hoverMode)
        {
            // Moving the open panel moves the bar's spot on the taskbar under it (by its nearer side).
            var wa = Screen.FromRectangle(Bounds).WorkingArea;
            bool right = Left + Width / 2 > wa.Left + wa.Width / 2;
            SavePill(right ? Right - PillSize.Width : Left);
            KeepOnScreen();
            if (bar != null) bar.Bounds = PillRect();
            restAt = Location;
            return;
        }
        Location = SnapRect(Bounds).Location;
        KeepOnScreen();
        openSpot = Bounds; restAt = Location;
    }

    // ---------- resizing and the snap preview ----------
    int MinW { get { return P(300); } }
    int MinH { get { return P(240); } }
    SnapGhost ghost;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_SIZING = 0x214, WM_MOVING = 0x216, WM_EXITSIZEMOVE = 0x232;
        if (m.Msg == WM_NCHITTEST && !collapsed && !anim.Enabled)
        {
            long lp = m.LParam.ToInt64();
            var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int g = Padding.Left + P(2);
            bool l = pt.X < g, r = pt.X >= Width - g, t = pt.Y < g, b = pt.Y >= Height - g;
            int code = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 0;
            if (code != 0) { m.Result = (IntPtr)code; return; }
        }
        else if (m.Msg == WM_SIZING)
        {
            // Keep it from getting too small to read.
            var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
            int edge = m.WParam.ToInt32();
            if (rc.R - rc.L < MinW) { if (edge == 1 || edge == 4 || edge == 7) rc.L = rc.R - MinW; else rc.R = rc.L + MinW; }
            if (rc.B - rc.T < MinH) { if (edge == 3 || edge == 4 || edge == 5) rc.T = rc.B - MinH; else rc.B = rc.T + MinH; }
            Marshal.StructureToPtr(rc, m.LParam, false);
        }
        else if (m.Msg == WM_MOVING)
        {
            // While dragging, show where it will stick if let go here.
            var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
            if (hoverMode && !collapsed)
            {
                // Opened from the taskbar: it slides left or right along it, and can't be pulled off it.
                var wa2 = Screen.FromRectangle(Bounds).WorkingArea; int pw2 = rc.R - rc.L;
                rc.L = Math.Max(wa2.Left, Math.Min(rc.L, wa2.Right - pw2)); rc.R = rc.L + pw2;
                rc.T = Top; rc.B = Bottom;
                Marshal.StructureToPtr(rc, m.LParam, false);
                if (bar != null)
                {
                    // The bar on the taskbar follows along under it.
                    bool rgt = rc.L + pw2 / 2 > wa2.Left + wa2.Width / 2;
                    var tb2 = TaskbarStrip(); int bx = rgt ? rc.R - bar.Width : rc.L;
                    bar.Left = Math.Max(tb2.Left, Math.Min(bx, tb2.Right - bar.Width));
                }
                m.Result = (IntPtr)1;
                return;
            }
            var r = new Rectangle(rc.L, rc.T, rc.R - rc.L, rc.B - rc.T);
            var s = SnapRect(r);
            if (s != r)
            {
                if (ghost == null) ghost = new SnapGhost();
                ghost.Bounds = s;
                if (!ghost.Visible) ghost.Show();
            }
            else if (ghost != null && ghost.Visible) ghost.Hide();
        }
        else if (m.Msg == 0x231)   // WM_ENTERSIZEMOVE: hold still while you drag or resize
            dragging = true;
        else if (m.Msg == WM_EXITSIZEMOVE)
        {
            dragging = false;
            if (ghost != null && ghost.Visible) ghost.Hide();
            if (!collapsed && !anim.Enabled) { expandedSize = Size; SavePlace(); if (!hoverMode) openSpot = Bounds; restAt = Location; }
        }
        base.WndProc(ref m);
    }

    void KeepOnScreen()
    {
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Max(wa.Left, Math.Min(Left, wa.Right - Width)), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height)));
    }

    // Saved as the corner it grows from, so it comes back in the same spot whatever its size.
    void LoadPlace()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width, wa.Bottom - Height);
        try
        {
            var parts = File.ReadAllText(placeFile).Split(',');
            if (parts[0] == "v2")
            {
                int ax = int.Parse(parts[1]), ay = int.Parse(parts[2]);
                bool right = parts[3] == "R", bottom = parts[4] == "B";
                if (parts.Length > 6) { expandedSize = new Size(Math.Max(MinW, int.Parse(parts[5])), Math.Max(MinH, int.Parse(parts[6]))); Size = expandedSize; }
                Location = new Point(right ? ax - Width : ax, bottom ? ay - Height : ay);
            }
            else Location = new Point(int.Parse(parts[0]) - Width, int.Parse(parts[1]) - Height);
            KeepOnScreen();
        }
        catch { }
    }

    public void SavePlace()
    {
        try
        {
            var wa = Screen.FromRectangle(Bounds).WorkingArea;
            bool right = Left + Width / 2 > wa.Left + wa.Width / 2, bottom = Top + Height / 2 > wa.Top + wa.Height / 2;
            File.WriteAllText(placeFile, "v2," + (right ? Right : Left) + "," + (bottom ? Bottom : Top) + "," + (right ? "R" : "L") + "," + (bottom ? "B" : "T") + "," + expandedSize.Width + "," + expandedSize.Height);
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}

// The see-through outline showing where the panel will stick when you let go of it.
class SnapGhost : Form
{
    public SnapGhost()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Ui.Accent; Opacity = 0.28;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x20 | 0x08000000;   // tool window, clicks pass through, never takes focus
            return cp;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
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
        new[] { "waiting", "SEEN, WAITING FOR YOU" } };

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
        if (k == "background") k = "working";
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
            // Open: brings up this account's Claude window, starting it if it's closed.
            var ob = new Rectangle(br.Left - P(62), br.Top, P(56), br.Height);
            bool ohov = canStart && ob.Contains(mouse);
            using (var p = Ui.Rounded(ob, P(6)))
            {
                if (ohov) using (var b = new SolidBrush(Ui.Hover)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.Line)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, "Open", fSmall, ob, canStart ? (ohov ? Ui.Text : Ui.Dim) : Ui.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (canStart) { int nn = n; hits.Add(new KeyValuePair<Rectangle, Action>(ob, () => { ClaudeTray.LaunchAccount(nn); })); }
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
        menu.Opened += (s, e) => Panel.MenusOpen++;
        menu.Closed += (s, e) => Panel.MenusOpen = Math.Max(0, Panel.MenusOpen - 1);
        menu.Show(this, new Point(at.Left, at.Bottom));
    }

    int DrawRow(Graphics g, Dictionary<string, object> it, string key, int x, int y, int w)
    {
        int h = RowHeight(it, key);
        var r = new Rectangle(x, y, w, h - P(2));
        string link = it["open"] as string;
        string rsid = it.ContainsKey("sessionId") ? it["sessionId"] as string : null;
        if (rsid != null && Data.OnScreen.Contains(rsid))
        {
            // The conversation open in that account's window: a faint warm tint, a little stronger in the window in front.
            bool front = Data.OnScreenFront.Contains(rsid);
            using (var p = Ui.Rounded(r, P(7))) using (var b = new SolidBrush(Color.FromArgb(front ? 30 : 16, Ui.Accent))) g.FillPath(b, p);
            using (var b = new SolidBrush(Color.FromArgb(front ? 170 : 90, Ui.Accent))) g.FillRectangle(b, r.X, r.Y + P(8), P(2), r.Height - P(16));
        }
        if (link != null)
        {
            if (r.Contains(mouse)) using (var p = Ui.Rounded(r, P(7))) using (var b = new SolidBrush(Ui.Hover)) g.FillPath(b, p);
            string osid = it.ContainsKey("sessionId") ? it["sessionId"] as string : null;
            hits.Add(new KeyValuePair<Rectangle, Action>(r, () => { if (osid != null) ClaudeTray.OpenConversation(osid); else ClaudeTray.Open(link); }));
        }
        string realKey = key == "pinned" ? (it["state"] as string ?? "idle") : key;
        // Answered, with a job of its own still running in the background: a steady blue dot.
        bool background = it["state"] as string == "background";
        if (realKey == "background") realKey = "working";
        bool rowHover = r.Contains(mouse);
        string sid = it.ContainsKey("sessionId") ? it["sessionId"] as string : null;
        // dot
        int dx = x + P(9), dy = y + P(11);
        var dotR = new RectangleF(dx, dy, P(7), P(7));
        if (realKey == "idle") using (var pen = new Pen(Ui.Faint, 1.5f * owner.S)) g.DrawEllipse(pen, dotR);
        else if (realKey == "working" && background)
            using (var b = new SolidBrush(Ui.Blue)) g.FillEllipse(b, dotR);
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
        bool showBtns = rowHover && sid != null;
        // With the three buttons showing, the title and the line under it stop short of them.
        if (showBtns) tw = w - P(24) - P(92);
        int detailW = showBtns ? tw : tw + P(48);
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
            TextRenderer.DrawText(g, (string)it["detail"], fSmall, new Rectangle(tx, ty, detailW, P(15)), Ui.Dim, flags);
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
        if (!(it["running"] is bool && (bool)it["running"]) ) meta.Add("closed");
        // Context size first and a little brighter than the rest of the line.
        int mx = tx;
        if (ctxText != null)
        {
            TextRenderer.DrawText(g, ctxText, fTiny, new Point(mx, ty), Ui.Dim, TextFormatFlags.NoPadding);
            mx += TextRenderer.MeasureText(g, ctxText, fTiny, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (meta.Count > 0) { TextRenderer.DrawText(g, " · ", fTiny, new Point(mx, ty), Ui.Faint, TextFormatFlags.NoPadding); mx += TextRenderer.MeasureText(g, " · ", fTiny, Size.Empty, TextFormatFlags.NoPadding).Width; }
        }
        TextRenderer.DrawText(g, string.Join(" · ", meta), fTiny, new Rectangle(mx, ty, Math.Max(0, tx + (showBtns && ty < y + P(30) ? tw : tw + P(48)) - mx), P(14)), Ui.Faint, flags);
        return h;
    }
}
