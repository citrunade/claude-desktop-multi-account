using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

// The accounts window: add an account, rename it, choose its icon, or remove it.
class ManageForm : Form
{
    static readonly string[] Kinds = { "tint", "claude", "number", "file" };
    static readonly string[] KindNames = { "Claude icon in a colour", "Claude icon, unchanged", "Number in a colour", "My own picture…" };
    static readonly int[] CapChoices = { 50, 60, 70, 80, 90, 95, 100 };
    static readonly int[] NotePercents = { 90, 95, 98 };
    static int CapIndex(int v) { int i = 0; for (int k = 0; k < CapChoices.Length; k++) if (CapChoices[k] <= v) i = k; return i; }
    static readonly string[] AutoModes = { "off", "ask", "auto", "send" };
    static readonly string[] AutoNames = { "Do nothing", "Ask me to move it to the freest account", "Move it, I press Enter", "Move it and send it by itself" };
    static readonly KeyValuePair<int, string>[] HideChoices =
    {
        new KeyValuePair<int, string>(30, "30 minutes"), new KeyValuePair<int, string>(60, "1 hour"),
        new KeyValuePair<int, string>(180, "3 hours"), new KeyValuePair<int, string>(720, "12 hours"),
        new KeyValuePair<int, string>(1440, "1 day"), new KeyValuePair<int, string>(4320, "3 days"),
        new KeyValuePair<int, string>(10080, "1 week"),
    };

    readonly FlowLayoutPanel list;
    readonly Label status;
    readonly Button addBtn;
    readonly float S;
    readonly ToolTip tip = new ToolTip();
    bool busy;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    // Dark title bar and a thin dark border to match the panel, instead of Windows' light frame.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Func<Color, int> cref = c => c.R | (c.G << 8) | (c.B << 16);
        int dark = 1, border = cref(Ui.Line), caption = cref(Ui.Bg), text = cref(Ui.Text), round = 2;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        try { DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { }
        try { DwmSetWindowAttribute(Handle, 35, ref caption, 4); } catch { }
        try { DwmSetWindowAttribute(Handle, 36, ref text, 4); } catch { }
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        // Stay hidden until the window is fully drawn, so it doesn't flash white first.
        int cloak = 1;
        try { DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref cloak, 4); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Refresh();
        try { DwmFlush(); } catch { }
        int cloak = 0;
        try { DwmSetWindowAttribute(Handle, 13, ref cloak, 4); } catch { }
    }

    public ManageForm()
    {
        using (var g = CreateGraphics()) S = g.DpiX / 96f;
        Text = "Claude accounts";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Ui.Bg; ForeColor = Ui.Text;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;   // always above the panel, which is itself always on top
        ClientSize = new Size(P(620), P(916));
        // Keep the whole window on screen (Windows can place it so the bottom buttons fall off).
        Shown += (s, e) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Max(wa.Left, Math.Min(Left, wa.Right - Width)), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height)));
        };

        var title = new Label { Text = "Your Claude accounts", Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(P(18), P(14)) };
        var hint = new Label
        {
            Text = "Each account opens from its own shortcut and stays signed in on its own. Change a name or icon, then Save.\n" +
                   "Account 1 is the normal Claude app: open it from its own shortcut to get your taskbar icon.",
            ForeColor = Ui.Dim, AutoSize = false, Location = new Point(P(18), P(42)), Size = new Size(P(584), P(64))
        };
        list = new FlowLayoutPanel
        {
            Location = new Point(P(12), P(110)), Size = new Size(P(596), P(348)), AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Bg
        };
        // How long a conversation waiting for you stays in the panel.
        var hideLabel = new Label { Text = "Stop showing a waiting conversation after", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(472)) };
        var current = Accounts.Load().hideAfterMinutes;
        int pick = 0;
        for (int i = 0; i < HideChoices.Length; i++) if (HideChoices[i].Key <= current) pick = i;
        var hide = MakeChoice(HideChoices.Select(c => c.Value).ToArray(), pick, P(120), idx =>
        {
            var st = Accounts.Load(); st.hideAfterMinutes = HideChoices[idx].Key; Accounts.Save(st);
        });
        hide.Location = new Point(P(290), P(466));
        var pinNote = new Label { Text = "(pinned ones always stay)", ForeColor = Ui.Faint, AutoSize = true, Location = new Point(P(418), P(472)) };

        // What to do when a conversation hits its usage limit.
        var autoLabel = new Label { Text = "When a conversation hits its limit", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(510)) };
        var mode = Accounts.Load().autoHandoff ?? "ask";
        var auto = MakeChoice(AutoNames, Math.Max(0, Array.IndexOf(AutoModes, mode)), P(310), idx =>
        {
            var st = Accounts.Load(); st.autoHandoff = AutoModes[idx]; Accounts.Save(st);
        });
        auto.Location = new Point(P(290), P(504));

        // Which account to move it to, and which accounts are too full to take it.
        var s0 = Accounts.Load();
        var byLabel = new Label { Text = "Move it to", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(548)) };
        // The same account the hand-off card marks as suggested.
        var by = new Label { Text = "the suggested account (hover for how it's picked)", ForeColor = Ui.Text, AutoSize = true, Location = new Point(P(290), P(548)) };
        tip.SetToolTip(by, "The same account the hand-off button marks as Suggested: one with room left in its 5 hours\n(counting chats already running there) whose weekly allowance is most likely to go unused\nbefore it resets. Close calls go to the one whose 5-hour limit resets sooner.");
        var capLabel = new Label { Text = "Skip an account once it has used", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(586)) };
        var pctNames = CapChoices.Select(c => c >= 100 ? "100% (until full)" : c + "%").ToArray();
        var capWeek = MakeChoice(pctNames.Select(x => x + " weekly").ToArray(), CapIndex(s0.handoffMaxWeekly), P(150), idx =>
        {
            var st = Accounts.Load(); st.handoffMaxWeekly = CapChoices[idx]; Accounts.Save(st);
        });
        capWeek.Location = new Point(P(290), P(580));
        var orLabel = new Label { Text = "or", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(446), P(586)) };
        var capFive = MakeChoice(pctNames.Select(x => x + " 5-hour").ToArray(), CapIndex(s0.handoffMaxFive), P(140), idx =>
        {
            var st = Accounts.Load(); st.handoffMaxFive = CapChoices[idx]; Accounts.Save(st);
        });
        capFive.Location = new Point(P(468), P(580));

        // The panel, separately for a Claude window in front and for anything else: opens on hover, or stays open.
        var modeNames = new[] { "Small bar, opens when the mouse is over it", "Stays open (– shrinks it, click the bar to reopen)" };
        var inLabel = new Label { Text = "While a Claude window is in front", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(630)) };
        var inClaude = MakeChoice(modeNames, s0.panelInClaude == "open" ? 1 : 0, P(330), idx =>
        {
            var st = Accounts.Load(); st.panelInClaude = idx == 1 ? "open" : "hover"; st.panelInClaudeShrunk = false; Accounts.Save(st);
        });
        inClaude.Location = new Point(P(270), P(624));
        var outLabel = new Label { Text = "While anything else is in front", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(668)) };
        var outside = MakeChoice(modeNames, s0.panelElsewhere == "open" ? 1 : 0, P(330), idx =>
        {
            var st = Accounts.Load(); st.panelElsewhere = idx == 1 ? "open" : "hover"; st.panelElsewhereShrunk = false; Accounts.Save(st);
        });
        outside.Location = new Point(P(270), P(662));
        var clockLabel = new Label { Text = "Start each account's 5-hour clock", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(706)) };
        var clock = MakeChoice(new[] { "Off", "Automatically, while I'm away from the PC", "Automatically, even while I'm using the PC" },
            !s0.keepClock ? 0 : s0.keepClockAnytime ? 2 : 1, P(330), idx =>
        {
            var st = Accounts.Load(); st.keepClock = idx > 0; st.keepClockAnytime = idx == 2; Accounts.Save(st);
        });
        clock.Location = new Point(P(270), P(700));
        tip.SetToolTip(clock, "When an account's 5-hour limit isn't counting down, it gets a one-line message in a new chat so the\n5 hours start now. This opens that chat and presses Enter. \"Away\" waits for 2 minutes without mouse or\nkeyboard use; \"even while using\" sends right away, pausing your mouse and keys for a moment. These chats\ndon't show in the panel.");
        var launchLabel = new Label { Text = "Over full-screen apps", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(744)) };
        var launch = MakeChoice(new[] { "Keep the bar showing", "Hide the bar" }, s0.hideOverGames ? 1 : 0, P(330), idx =>
        {
            var st = Accounts.Load(); st.hideOverGames = idx == 1; Accounts.Save(st);
        });
        launch.Location = new Point(P(270), P(738));
        tip.SetToolTip(launch, "While a game, video or other app fills the whole screen, the taskbar bar either stays where it is\n(it doesn't move or re-raise itself during the game) or is taken away until you leave it.");
        // Near the 5-hour limit: finish the step, write a hand-off note, stop.
        var noteLabel = new Label { Text = "Near a chat's 5-hour limit", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(782)) };
        int notePick = !s0.handoffNote ? 0 : Math.Max(1, Array.IndexOf(NotePercents, s0.handoffNotePercent) + 1);
        if (s0.handoffNote && Array.IndexOf(NotePercents, s0.handoffNotePercent) < 0) notePick = 2;
        var note = MakeChoice(new[] { "Keep going" }.Concat(NotePercents.Select(v => "At " + v + "%, write a hand-off note and stop")).ToArray(), notePick, P(330), idx =>
        {
            var st = Accounts.Load(); st.handoffNote = idx > 0; if (idx > 0) st.handoffNotePercent = NotePercents[idx - 1]; Accounts.Save(st);
        });
        note.Location = new Point(P(270), P(776));
        tip.SetToolTip(note, "When a chat's account has used this much of its 5-hour limit, Claude finishes the step it is on,\nwrites a short note (the goal, what was decided and why, what is done and half-done, the next step)\nand stops. Moving that chat to another account then hands the new chat this note, so nothing\ndecided earlier gets lost. It asks once per chat in each 5-hour window; if you carry on, it lets you.\nThe last few percent are kept so there is room to write the note.");
        // Before Claude forgets a quiet chat (it remembers one cheaply for an hour), have it write its note.
        var quietLabel = new Label { Text = "Before a quiet chat is forgotten", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(18), P(820)) };
        var quiet = MakeChoice(new[] { "Do nothing", "After 55 minutes, write a hand-off note" }, s0.noteBeforeForgotten ? 1 : 0, P(330), idx =>
        {
            var st = Accounts.Load(); st.noteBeforeForgotten = idx == 1; Accounts.Save(st);
        });
        quiet.Location = new Point(P(270), P(814));
        tip.SetToolTip(quiet, "Claude remembers a chat cheaply for an hour after its last message. After that, carrying it on\nmeans reading the whole chat back in, which uses far more of your limit. So a chat left alone for\n55 minutes is asked to write its short hand-off note while that is still cheap (about 2% of a 5-hour\nlimit). Only bigger chats that are still open, once each time they go quiet; you'll see the note\nmessage in that chat.");
        Controls.AddRange(new Control[] { inLabel, inClaude, outLabel, outside, clockLabel, clock, launchLabel, launch, noteLabel, note, quietLabel, quiet });

        addBtn = MakeButton("+ Add account", P(130));
        addBtn.Location = new Point(P(18), P(866));
        // Adding an account opens it straight away on Claude's sign-in screen.
        addBtn.Click += (s, e) =>
        {
            AllowSetForegroundWindow(-1);
            Run("Adding an account…", () =>
            {
                var a = Accounts.AddNew();
                var exe = Accounts.LauncherPath(a.n);
                if (exe != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
                return Accounts.ShortcutName(a) + " added and opening: sign in to the new account there.";
            });
        };
        status = new Label { ForeColor = Ui.Dim, AutoSize = false, Location = new Point(P(160), P(864)), Size = new Size(P(440), P(40)), TextAlign = ContentAlignment.MiddleLeft };
        Controls.AddRange(new Control[] { title, hint, list, hideLabel, hide, pinNote, autoLabel, auto, byLabel, by, capLabel, capWeek, orLabel, capFive, addBtn, status });
        Reload();
    }

    int P(float v) { return (int)Math.Round(v * S); }

    // A dark button that opens a small menu of choices; picking one saves it straight away.
    Button MakeChoice(string[] labels, int pick, int width, Action<int> save)
    {
        var b = MakeButton(labels[pick] + "  \u25BE", width); b.Height = P(28);
        var menu = DarkMenuColors.NewMenu(Font);
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem(labels[i]) { ForeColor = Ui.Text };
            item.Click += (s, e) =>
            {
                save(idx);
                b.Text = labels[idx] + "  \u25BE";
                status.ForeColor = Ui.Ok; status.Text = "Saved. The panel uses it from its next refresh.";
            };
            menu.Items.Add(item);
        }
        b.Click += (s, e) => menu.Show(b, new Point(0, b.Height));
        return b;
    }

    Button MakeButton(string text, int width)
    {
        var b = new DarkButton { Text = text, Width = width, Height = P(30), FlatStyle = FlatStyle.Flat, BackColor = Ui.Card, ForeColor = Ui.Text, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = Ui.Line; b.FlatAppearance.MouseOverBackColor = Ui.Hover;
        return b;
    }

    void Reload()
    {
        list.SuspendLayout();
        foreach (Control c in list.Controls) c.Dispose();
        list.Controls.Clear();
        foreach (var a in Accounts.All()) list.Controls.Add(MakeRow(a));
        list.ResumeLayout();
    }

    // Long jobs (building a launcher) run off this window's thread.
    void Run(string doing, Func<string> work)
    {
        if (busy) return;
        busy = true; UseWaitCursor = true; status.ForeColor = Ui.Dim; status.Text = doing;
        foreach (Control c in Controls) if (c != status) c.Enabled = false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string msg; bool ok = true;
            try { msg = work(); } catch (Exception e) { msg = e.Message; ok = false; }
            BeginInvoke((Action)(() =>
            {
                busy = false; UseWaitCursor = false;
                foreach (Control c in Controls) c.Enabled = true;
                status.ForeColor = ok ? Ui.Ok : Ui.Bad; status.Text = msg;
                Reload();
            }));
        });
    }

    Control MakeRow(AccountInfo saved)
    {
        var a = new AccountInfo { n = saved.n, name = saved.name, icon = saved.icon ?? "tint", color = saved.color, file = saved.file, appId = saved.appId };
        var row = new System.Windows.Forms.Panel { Width = P(570), Height = P(64), BackColor = Ui.Card, Margin = new Padding(P(4), P(3), P(4), P(3)) };

        var pic = new PictureBox { Location = new Point(P(24), P(10)), Size = new Size(P(44), P(44)), SizeMode = PictureBoxSizeMode.Zoom };
        var name = new TextBox
        {
            Location = new Point(P(76), P(8)), Width = P(144), BackColor = Ui.Bg, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle,
            Text = string.IsNullOrWhiteSpace(a.name) ? "Account " + a.n : a.name
        };
        var num = new Label { Text = a.n == 1 ? "Account 1 · normal Claude app" : "Account " + a.n, ForeColor = Ui.Faint, Location = new Point(P(76), P(38)), AutoSize = true, Font = new Font("Segoe UI", 8f) };
        // Icon style: four small previews of this account's icon, click one to pick it.
        var tiles = new List<IconTile>();
        for (int i = 0; i < Kinds.Length; i++)
        {
            var t = new IconTile { Kind = Kinds[i], Location = new Point(P(228) + i * P(36), P(4)), Size = new Size(P(32), P(28)) };
            tip.SetToolTip(t, KindNames[i]);
            tiles.Add(t);
        }
        var colour = MakeButton("Colour", P(80)); colour.Location = new Point(P(228), P(34)); colour.Height = P(24);
        var picture = MakeButton("Choose…", P(80)); picture.Location = new Point(P(312), P(34)); picture.Height = P(24);
        var save = MakeButton("Save", P(70)); save.Location = new Point(P(410), P(8)); save.Enabled = false;
        var remove = MakeButton("Remove", P(70)); remove.Location = new Point(P(488), P(8)); remove.Visible = a.n > 1;
        remove.ForeColor = Ui.Bad;
        // Open this account by itself right after you sign in to Windows. Saved straight away.
        var startup = new CheckBox
        {
            Text = "Open when Windows starts", ForeColor = Ui.Dim, AutoSize = true, Location = new Point(P(410), P(38)),
            Font = new Font("Segoe UI", 8f), Checked = (Accounts.Load().autoLaunchAccounts ?? new List<int>()).Contains(a.n)
        };
        startup.CheckedChanged += (s, e) =>
        {
            var st = Accounts.Load();
            if (st.autoLaunchAccounts == null) st.autoLaunchAccounts = new List<int>();
            st.autoLaunchAccounts.Remove(a.n);
            if (startup.Checked) st.autoLaunchAccounts.Add(a.n);
            Accounts.Save(st);
            status.ForeColor = Ui.Ok; status.Text = Accounts.ShortcutName(a) + (startup.Checked ? " will open when Windows starts." : " won't open by itself.");
        };

        // Order: arrows move this account up or down in the panel and in this list.
        var all = Accounts.All();
        int at = all.FindIndex(x => x.n == a.n);
        var up = MakeArrow("", at > 0); up.Location = new Point(P(2), P(8));
        var down = MakeArrow("", at >= 0 && at < all.Count - 1); down.Location = new Point(P(2), P(34));
        tip.SetToolTip(up, "Move up"); tip.SetToolTip(down, "Move down");
        up.Click += (s, e) => Reorder(a, -1);
        down.Click += (s, e) => Reorder(a, 1);

        Action refresh = () =>
        {
            var old = pic.Image; pic.Image = Accounts.Render(a, P(44)); if (old != null) old.Dispose();
            colour.Enabled = a.icon == "tint" || a.icon == "number";
            colour.BackColor = colour.Enabled ? Accounts.ParseColor(a.color, a.n) : Ui.Card;
            colour.ForeColor = Color.White;
            picture.Enabled = a.icon == "file";
            foreach (var t in tiles)
            {
                var v = new AccountInfo { n = a.n, icon = t.Kind, color = a.color, file = a.file };
                t.SetImage(t.Kind == "file" && string.IsNullOrEmpty(a.file) ? null : Accounts.Render(v, P(22)));
                t.Selected = t.Kind == a.icon;
            }
        };
        Action dirty = () => { save.Enabled = true; refresh(); };

        name.TextChanged += (s, e) => { a.name = name.Text.Trim() == "Account " + a.n ? null : name.Text.Trim(); save.Enabled = true; };
        foreach (var t in tiles)
        {
            var tt = t;
            tt.Click += (s, e) =>
            {
                if (tt.Kind == "file" && string.IsNullOrEmpty(a.file) && !PickPicture(a)) return;
                a.icon = tt.Kind;
                dirty();
            };
        }
        colour.Click += (s, e) =>
        {
            using (var cd = new ColorDialog { Color = Accounts.ParseColor(a.color, a.n), FullOpen = true, CustomColors = Accounts.Palette.Select(h => { var c = ColorTranslator.FromHtml(h); return c.R | (c.G << 8) | (c.B << 16); }).ToArray() })
                if (cd.ShowDialog(this) == DialogResult.OK) { a.color = ColorTranslator.ToHtml(cd.Color); dirty(); }
        };
        picture.Click += (s, e) => { if (PickPicture(a)) dirty(); };
        save.Click += (s, e) => Run("Saving " + Accounts.ShortcutName(a) + "…", () => { Accounts.Apply(a); return "Saved. Shortcuts and taskbar icon updated."; });
        remove.Click += (s, e) =>
        {
            var r = MessageBox.Show(this,
                "Remove " + Accounts.ShortcutName(a) + "? If it's open, it will be closed first.\n\nYes: remove its shortcuts and also sign it out (deletes its saved sign-in and settings).\n" +
                "No: remove its shortcuts but keep the sign-in, so adding it back later finds it signed in.\nCancel: keep everything.",
                "Remove account", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Cancel) return;
            bool signOut = r == DialogResult.Yes;
            Run("Removing…", () => { Accounts.Remove(a, signOut); return Accounts.ShortcutName(a) + " removed" + (signOut ? " and signed out." : "; its sign-in is kept."); });
        };

        row.Controls.AddRange(new Control[] { up, down, pic, name, num, colour, picture, save, remove, startup });
        row.Controls.AddRange(tiles.ToArray());
        refresh();
        return row;
    }

    Button MakeArrow(string glyph, bool enabled)
    {
        var b = new DarkButton
        {
            Text = glyph, Font = new Font("Segoe MDL2 Assets", 7.5f), Size = new Size(P(20), P(22)), FlatStyle = FlatStyle.Flat,
            BackColor = Ui.Card, ForeColor = Ui.Dim, Visible = enabled, Cursor = Cursors.Hand, TabStop = false,   // no arrow where it can't move
        };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Ui.Hover;
        return b;
    }

    // The list is rebuilt to show the new order, which would drop unsaved name or icon edits: save those first.
    void Reorder(AccountInfo a, int step)
    {
        foreach (Control row in list.Controls)
            foreach (Control c in row.Controls)
                if (c is Button && c.Text == "Save" && c.Enabled)
                {
                    status.ForeColor = Ui.Warn; status.Text = "Save or undo your changes first, then move it.";
                    return;
                }
        Accounts.MoveInOrder(a.n, step);
        int scroll = list.VerticalScroll.Value;
        Reload();
        try { list.AutoScrollPosition = new Point(0, scroll); } catch { }
        status.ForeColor = Ui.Ok; status.Text = "Order saved. The panel uses it from its next refresh.";
    }

    bool PickPicture(AccountInfo a)
    {
        using (var fd = new OpenFileDialog { Title = "Choose a picture for this account", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|All files|*.*" })
        {
            if (fd.ShowDialog(this) != DialogResult.OK) return false;
            // Keep a copy next to the account, so the icon survives the original being moved.
            try
            {
                var dir = Accounts.AccountDir(a.n);
                System.IO.Directory.CreateDirectory(dir);
                var copy = System.IO.Path.Combine(dir, "picture" + System.IO.Path.GetExtension(fd.FileName).ToLowerInvariant());
                if (!string.Equals(copy, fd.FileName, StringComparison.OrdinalIgnoreCase)) System.IO.File.Copy(fd.FileName, copy, true);
                a.file = copy;
            }
            catch { a.file = fd.FileName; }
            return true;
        }
    }
}

// A flat dark button whose greyed-out text stays readable. Windows draws a disabled button's text in
// its own near-black grey, which disappears on the dark background, so that case is drawn here.
class DarkButton : Button
{
    public static readonly Color DisabledText = Color.FromArgb(128, 125, 117);
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        var g = e.Graphics;
        using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        if (FlatAppearance.BorderSize > 0)
            using (var pen = new Pen(FlatAppearance.BorderColor.IsEmpty ? Ui.Line : FlatAppearance.BorderColor))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, DisabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

// One clickable icon preview: highlighted when it's the chosen style.
class IconTile : Control
{
    public string Kind;
    Image img;
    bool selected, hover;
    public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Invalidate(); } } }

    public IconTile()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        BackColor = Ui.Card;
    }
    public void SetImage(Image i) { var old = img; img = i; if (old != null) old.Dispose(); Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var p = Accounts.Rounded(r, 6))
        {
            if (selected || hover) using (var b = new SolidBrush(selected ? Color.FromArgb(52, 51, 47) : Ui.Hover)) g.FillPath(b, p);
            using (var pen = new Pen(selected ? Ui.Accent : (hover ? Color.FromArgb(70, 69, 64) : Ui.Line), selected ? 1.8f : 1f)) g.DrawPath(pen, p);
        }
        int s = Math.Min(Width, Height) - 8;
        var dst = new Rectangle((Width - s) / 2, (Height - s) / 2, s, s);
        if (img != null) g.DrawImage(img, dst);
        else using (var f = new Font("Segoe MDL2 Assets", 10f))
            TextRenderer.DrawText(g, "\uE8B9", f, dst, Ui.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);   // picture glyph
    }
}

// Dark colours for the small drop-down menu in the accounts window.
class DarkMenuColors : ProfessionalColorTable
{
    public static ContextMenuStrip NewMenu(Font font)
    {
        return new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()), ShowImageMargin = false, BackColor = Ui.Card, ForeColor = Ui.Text, Font = font };
    }
    public override Color ToolStripDropDownBackground { get { return Ui.Card; } }
    public override Color MenuBorder { get { return Ui.Line; } }
    public override Color MenuItemBorder { get { return Ui.Hover; } }
    public override Color MenuItemSelected { get { return Ui.Hover; } }
    public override Color ImageMarginGradientBegin { get { return Ui.Card; } }
    public override Color ImageMarginGradientMiddle { get { return Ui.Card; } }
    public override Color ImageMarginGradientEnd { get { return Ui.Card; } }
}
