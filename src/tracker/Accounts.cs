using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

// The accounts you have set up: names, icons, launchers and shortcuts.
//
// Account 1 is the normal Claude app. Accounts 2 and up each have a small launcher in
// %USERPROFILE%\Apps\Claude Account N that starts Claude with its own data folder (%APPDATA%\Claude-N),
// so each stays signed in on its own and gets its own taskbar button.
public class AccountInfo
{
    public int n;
    public string name;          // shown in the panel; the shortcut is "Claude <name>"
    public string icon = "tint"; // tint | claude | number | file
    public string color;         // #RRGGBB, for tint and number
    public string file;          // picture, for "file"
    public string appId;         // taskbar group id; kept as-is for launchers made before this program
}

public class AccountSettings
{
    public List<AccountInfo> accounts = new List<AccountInfo>();
    // How long a conversation waiting for you stays in the panel (pinned ones always stay).
    public int hideAfterMinutes = 60;
    // Close the background helper of a conversation that has sat idle longer than hideAfterMinutes
    // (at least 15 minutes) to free its memory. The app starts a new one when you use it again.
    public bool closeIdleHelpers = true;
    // How the panel behaves while a Claude window is in front, and while anything else is:
    //   "hover": a small bar that opens while the mouse is over it;
    //   "open":  stays open over other windows; the – button shrinks it to the bar, a click on the bar reopens it.
    public string panelInClaude = "open";
    public string panelElsewhere = "hover";
    // In "open" mode, whether you shrank it there (remembered).
    public bool panelInClaudeShrunk = false;
    public bool panelElsewhereShrunk = false;
    // Send each account a one-line message when its 5-hour window isn't running, so the clock starts.
    public bool keepClock = false;
    // When a conversation hits a usage limit: "off", "ask" (a notification to click) or "auto".
    public string autoHandoff = "ask";
    // Which account counts as freest: "weekly", "five" (5-hour) or "both" (the fuller of the two decides).
    public string handoffPickBy = "both";
    // Never hand off to an account that has used this much (percent) of its weekly / 5-hour limit.
    public int handoffMaxWeekly = 90;
    public int handoffMaxFive = 90;
}

static class Accounts
{
    public static readonly string TrackerDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    // Launchers live next to Claude Tracker's own folder (normally %USERPROFILE%\Apps).
    public static readonly string AppsRoot = Path.GetDirectoryName(TrackerDir);
    static readonly string SettingsFile = Path.Combine(TrackerDir, "accounts.json");
    // For testing the installer: CLAUDE_ACCOUNTS_SHORTCUT_ROOT puts every shortcut under one folder instead.
    static readonly string TestRoot = Environment.GetEnvironmentVariable("CLAUDE_ACCOUNTS_SHORTCUT_ROOT");
    public static readonly string StartMenuDir = TestRoot != null ? Path.Combine(TestRoot, @"Programs\Claude Accounts") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Accounts");
    static readonly string DesktopDir = TestRoot != null ? Path.Combine(TestRoot, "Desktop") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    static readonly string StartupDir = TestRoot != null ? Path.Combine(TestRoot, "Startup") : Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    static readonly string TaskbarDir = TestRoot != null ? Path.Combine(TestRoot, "TaskBar") : Path.Combine(Data.AppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
    // ...and CLAUDE_ACCOUNTS_SETTINGS points the limits-plugin registration at a copy of Claude Code's settings.
    static string ClaudeSettingsPath() { return Environment.GetEnvironmentVariable("CLAUDE_ACCOUNTS_SETTINGS") ?? Path.Combine(Data.ClaudeDir, "settings.json"); }
    public static readonly string[] Palette = { "#D97757", "#4F7FD9", "#4FA36B", "#9A6BD9", "#D9558F", "#2FA5A5", "#D9A23B", "#6B7280", "#B5533C" };

    // ---------- settings ----------
    public static AccountSettings Load()
    {
        AccountSettings s = null;
        try { if (File.Exists(SettingsFile)) s = new JavaScriptSerializer().Deserialize<AccountSettings>(File.ReadAllText(SettingsFile)); } catch { }
        s = s ?? new AccountSettings();
        if (s.accounts == null) s.accounts = new List<AccountInfo>();
        return s;
    }
    public static void Save(AccountSettings s)
    {
        s.accounts = s.accounts.OrderBy(a => a.n).ToList();
        File.WriteAllText(SettingsFile, new JavaScriptSerializer().Serialize(s));
    }

    public static string NameOf(AccountSettings s, int n)
    {
        var a = s.accounts.FirstOrDefault(x => x.n == n);
        return a != null && !string.IsNullOrWhiteSpace(a.name) ? a.name : "Account " + n;
    }
    public static string ShortcutName(AccountInfo a) { return "Claude " + (string.IsNullOrWhiteSpace(a.name) ? "Account " + a.n : a.name.Trim()); }

    public static string AccountDir(int n) { return Path.Combine(AppsRoot, "Claude Account " + n); }
    // Windows caches icons by file name, so every change gets a new file (icon-<time>.ico).
    public static string IconPath(int n)
    {
        var dir = AccountDir(n);
        try
        {
            var newest = new DirectoryInfo(dir).GetFiles("icon*.ico").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest != null) return newest.FullName;
        }
        catch { }
        return Path.Combine(dir, "icon.ico");
    }
    static string FreshIconPath(int n)
    {
        var dir = AccountDir(n);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "icon-" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".ico");
        foreach (var f in Directory.GetFiles(dir, "icon*.ico"))
            if (!string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
        return path;
    }

    public static string LauncherPath(int n)
    {
        foreach (var p in new[] { Path.Combine(AccountDir(n), "Claude Account " + n + ".exe"),
                                  Path.Combine(Data.Local, "Claude-" + n, "Claude Account " + n + ".exe") })
            if (File.Exists(p)) return p;
        return null;
    }

    // Accounts known from settings plus any Claude-N data folder or launcher found on this PC.
    public static List<AccountInfo> All()
    {
        var s = Load();
        var list = s.accounts.ToList();
        var nums = new HashSet<int>(list.Select(a => a.n));
        foreach (var p in Data.Profiles())
            if (nums.Add(p.N)) list.Add(new AccountInfo { n = p.N, color = Palette[(p.N - 1) % Palette.Length] });
        if (nums.Add(1)) list.Add(new AccountInfo { n = 1, color = Palette[0] });
        return list.OrderBy(a => a.n).ToList();
    }

    // ---------- icons ----------
    static string claudeLogo;
    // The Claude app's own tile (orange square, white spark), taken from the installed app.
    public static string ClaudeLogo()
    {
        if (claudeLogo != null && File.Exists(claudeLogo)) return claudeLogo;
        var cached = Path.Combine(TrackerDir, "claude-logo.png");
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                if (k != null)
                    foreach (var name in k.GetSubKeyNames().Where(x => x.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x))
                        using (var pk = k.OpenSubKey(name))
                        {
                            var root = pk == null ? null : pk.GetValue("PackageRootFolder") as string;
                            if (root == null) continue;
                            foreach (var f in new[] { @"assets\Square44x44Logo.targetsize-256_altform-unplated.png", @"assets\Square44x44Logo.targetsize-256.png", @"assets\Square310x310Logo.png" })
                            {
                                var full = Path.Combine(root, f);
                                if (File.Exists(full))
                                {
                                    try { File.Copy(full, cached, true); return claudeLogo = cached; }
                                    catch { return claudeLogo = full; }
                                }
                            }
                        }
        }
        catch { }
        return File.Exists(cached) ? (claudeLogo = cached) : null;
    }

    public static Color ParseColor(string hex, int n)
    {
        try { return ColorTranslator.FromHtml(string.IsNullOrEmpty(hex) ? Palette[(n - 1) % Palette.Length] : hex); }
        catch { return ColorTranslator.FromHtml(Palette[(n - 1) % Palette.Length]); }
    }

    // One picture of the icon at a given size.
    public static Bitmap Render(AccountInfo a, int size)
    {
        var col = ParseColor(a.color, a.n);
        string kind = a.icon ?? "tint";
        if ((kind == "tint" || kind == "claude") && ClaudeLogo() == null) kind = "number";
        if (kind == "file" && (string.IsNullOrEmpty(a.file) || !File.Exists(a.file))) kind = "number";

        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            if (kind == "tint" || kind == "claude")
            {
                using (var logo = (Bitmap)Image.FromFile(ClaudeLogo()))
                {
                    if (kind == "tint")
                        using (var t = Tint(logo, col)) g.DrawImage(t, 0, 0, size, size);
                    else g.DrawImage(logo, 0, 0, size, size);
                }
            }
            else if (kind == "file")
            {
                using (var img = LoadPicture(a.file, size))
                {
                    float sc = Math.Min((float)size / img.Width, (float)size / img.Height);
                    float w = img.Width * sc, h = img.Height * sc;
                    g.DrawImage(img, (size - w) / 2, (size - h) / 2, w, h);
                }
            }
            else
            {
                using (var path = Rounded(new RectangleF(0, 0, size - 1, size - 1), size * 0.22f))
                using (var b = new SolidBrush(col)) g.FillPath(b, path);
                using (var f = new Font("Segoe UI", size * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(a.n.ToString(), f, Brushes.White, new RectangleF(0, size * 0.03f, size, size), sf);
            }
        }
        return bmp;
    }

    static Image LoadPicture(string file, int size)
    {
        if (file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            var fromIco = BiggestIconImage(file);
            if (fromIco != null) return fromIco;
        }
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            try { using (var ico = Icon.ExtractAssociatedIcon(file)) return ico.ToBitmap(); } catch { }
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read)) return new Bitmap(Image.FromStream(fs));
    }

    // The largest picture inside an .ico, including the PNG-compressed kind that Icon.ToBitmap mangles.
    static Image BiggestIconImage(string file)
    {
        try
        {
            var b = File.ReadAllBytes(file);
            int count = BitConverter.ToUInt16(b, 4), best = -1, bestSize = -1;
            for (int i = 0; i < count; i++)
            {
                int sz = b[6 + 16 * i]; if (sz == 0) sz = 256;
                if (sz > bestSize) { bestSize = sz; best = i; }
            }
            if (best < 0) return null;
            int len = BitConverter.ToInt32(b, 6 + 16 * best + 8), off = BitConverter.ToInt32(b, 6 + 16 * best + 12);
            bool png = b[off] == 0x89 && b[off + 1] == 0x50 && b[off + 2] == 0x4E && b[off + 3] == 0x47;
            if (png) using (var ms = new MemoryStream(b, off, len)) return new Bitmap(Image.FromStream(ms));
            using (var ico = new Icon(file, bestSize, bestSize)) return ico.ToBitmap();
        }
        catch { return null; }
    }

    // Recolour the Claude tile: the orange becomes the chosen colour, the white spark stays white.
    static Bitmap Tint(Bitmap src, Color c)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        using (var s32 = src.Clone(rect, PixelFormat.Format32bppArgb))
        {
            var sd = s32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var buf = new byte[sd.Stride * src.Height];
            System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, buf, 0, buf.Length);
            for (int i = 0; i < buf.Length; i += 4)
            {
                int bl = buf[i], gr = buf[i + 1], rd = buf[i + 2];
                // how white this pixel is: the tile's green channel sits near 110-125, the spark near 240
                double w = Math.Max(0, Math.Min(1, (gr - 125) / 110.0));
                double shade = Math.Max(0.8, Math.Min(1.15, rd / 217.0));   // keep the tile's soft gradient
                buf[i + 2] = (byte)Math.Min(255, c.R * shade * (1 - w) + 255 * w);
                buf[i + 1] = (byte)Math.Min(255, c.G * shade * (1 - w) + 255 * w);
                buf[i] = (byte)Math.Min(255, c.B * shade * (1 - w) + 255 * w);
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, dd.Scan0, buf.Length);
            s32.UnlockBits(sd); dst.UnlockBits(dd);
        }
        return dst;
    }

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // .ico file holding PNG images at several sizes.
    public static void WriteIcon(AccountInfo a, string path)
    {
        var sizes = new[] { 256, 64, 48, 32, 24, 16 };
        var pngs = new List<byte[]>();
        foreach (var s in sizes)
            using (var b = Render(a, s))
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(d); w.Write(d); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32); w.Write((uint)pngs[i].Length); w.Write((uint)offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }

    // ---------- launchers ----------
    static string Csc()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in new[] { @"Microsoft.NET\Framework64\v4.0.30319\csc.exe", @"Microsoft.NET\Framework\v4.0.30319\csc.exe" })
            if (File.Exists(Path.Combine(win, p))) return Path.Combine(win, p);
        throw new Exception("The .NET Framework 4 compiler that comes with Windows wasn't found.");
    }

    static string LauncherSource()
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("launcher.cs"))
        using (var r = new StreamReader(s)) return r.ReadToEnd();
    }

    static List<Process> ProcessesAt(string exe)
    {
        var outp = new List<Process>();
        foreach (var p in Process.GetProcesses())
            try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) outp.Add(p); } catch { }
        return outp;
    }

    // Build (or rebuild) account n's launcher with its current icon and name.
    static void BuildLauncher(AccountInfo a)
    {
        var dir = AccountDir(a.n);
        var exe = Path.Combine(dir, "Claude Account " + a.n + ".exe");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name.txt"), ShortcutName(a));
        File.WriteAllText(Path.Combine(dir, "appid.txt"), string.IsNullOrEmpty(a.appId) ? "ClaudeAccounts.Account" + a.n : a.appId);
        File.WriteAllText(Path.Combine(dir, "iconfile.txt"), IconPath(a.n));

        // A running launcher can't be overwritten, but it can be moved aside.
        bool wasWatching = false;
        if (File.Exists(exe))
        {
            var running = ProcessesAt(exe);
            wasWatching = running.Count > 0;
            foreach (var p in running) try { p.Kill(); p.WaitForExit(2000); } catch { }
            var old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { old = exe + "." + DateTime.Now.Ticks + ".old"; }
            try { File.Move(exe, old); } catch { }
        }
        var src = Path.Combine(Path.GetTempPath(), "claude-account-" + a.n + ".cs");
        File.WriteAllText(src, LauncherSource().Replace("__NUMBER__", a.n.ToString()));
        var psi = new ProcessStartInfo(Csc(), "/nologo /target:winexe /optimize /codepage:65001 \"/win32icon:" + IconPath(a.n) + "\" /r:System.Management.dll /r:System.Windows.Forms.dll \"/out:" + exe + "\" \"" + src + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi))
        {
            var log = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("Couldn't build the launcher for account " + a.n + ":\n" + log);
        }
        try { File.Delete(src); } catch { }
        // If the account was open, keep its windows on their own taskbar button with the new icon.
        if (wasWatching) try { Process.Start(new ProcessStartInfo(exe, "--watch-only") { UseShellExecute = false }); } catch { }
    }

    // ---------- shortcuts ----------
    static string TargetOf(AccountInfo a) { return Path.Combine(AccountDir(a.n), "Claude Account " + a.n + ".exe"); }

    static void MakeShortcut(string lnk, string target, string args, string icon, string workdir, string desc, string appId = null)
    {
        MakeShortcutFile(lnk, target, args, icon, workdir, desc);
        ShellLinkId.Set(lnk, appId);
    }
    static void MakeShortcutFile(string lnk, string target, string args, string icon, string workdir, string desc)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        dynamic sh = Activator.CreateInstance(t);
        dynamic k = sh.CreateShortcut(lnk);
        k.TargetPath = target; k.Arguments = args ?? ""; k.IconLocation = icon + ",0";
        if (workdir != null) k.WorkingDirectory = workdir;
        k.Description = desc; k.Save();
        System.Runtime.InteropServices.Marshal.ReleaseComObject(k); System.Runtime.InteropServices.Marshal.ReleaseComObject(sh);
    }

    // Shortcuts that belong to account n, wherever they are (old names included).
    static IEnumerable<string> ShortcutsOf(AccountInfo a)
    {
        var dir = AccountDir(a.n).ToLowerInvariant();
        var launcher = TargetOf(a).ToLowerInvariant();
        var t = Type.GetTypeFromProgID("WScript.Shell");
        dynamic sh = Activator.CreateInstance(t);
        foreach (var folder in new[] { DesktopDir, StartMenuDir, Path.GetDirectoryName(StartMenuDir), TaskbarDir })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var lnk in Directory.GetFiles(folder, "*.lnk"))
            {
                string target = "", icon = "";
                try { dynamic k = sh.CreateShortcut(lnk); target = ((string)k.TargetPath ?? "").ToLowerInvariant(); icon = ((string)k.IconLocation ?? "").ToLowerInvariant(); }
                catch { continue; }
                bool mine = target == launcher || (a.n == 1 && icon.StartsWith(dir + "\\"));
                if (mine) yield return lnk;
            }
        }
    }

    static void RefreshShortcuts(AccountInfo a)
    {
        var target = TargetOf(a);
        var dir = AccountDir(a.n);
        var name = ShortcutName(a);
        var appId = a.appId;
        foreach (var lnk in ShortcutsOf(a).ToList())
        {
            var folder = Path.GetDirectoryName(lnk);
            if (string.Equals(folder, TaskbarDir, StringComparison.OrdinalIgnoreCase))
            {   // keep pins where they are, just give them the new icon
                try { MakeShortcut(lnk, target, null, IconPath(a.n), dir, name, appId); } catch { }
                continue;
            }
            try { File.Delete(lnk); } catch { }
        }
        Directory.CreateDirectory(StartMenuDir);
        Directory.CreateDirectory(DesktopDir);
        MakeShortcut(Path.Combine(DesktopDir, name + ".lnk"), target, null, IconPath(a.n), dir, name, appId);
        MakeShortcut(Path.Combine(StartMenuDir, name + ".lnk"), target, null, IconPath(a.n), dir, name, appId);
        RefreshShellIcons();
        // Ask Windows to reload its icon cache (the taskbar otherwise keeps showing the old picture).
        if (TestRoot == null)
            try { using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ie4uinit.exe"), "-show") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(5000); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
    static void RefreshShellIcons() { try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }

    // ---------- whole operations ----------
    // Make or update one account: icon, launcher, data folder, shortcuts.
    public static void Apply(AccountInfo a)
    {
        if (string.IsNullOrEmpty(a.color)) a.color = Palette[(a.n - 1) % Palette.Length];
        // The taskbar remembers a button's icon per group id, so each save gets a fresh id.
        a.appId = "ClaudeAccounts.Account" + a.n + "." + DateTime.Now.ToString("yyMMddHHmmss");
        WriteIcon(a, FreshIconPath(a.n));
        // Every account, Account 1 too, opens through a small launcher that gives its windows their taskbar icon.
        BuildLauncher(a);
        if (a.n > 1) Directory.CreateDirectory(Path.Combine(Data.AppData, "Claude-" + a.n));
        RefreshShortcuts(a);
        var s = Load();
        s.accounts.RemoveAll(x => x.n == a.n);
        s.accounts.Add(a);
        Save(s);
    }

    public static AccountInfo AddNew()
    {
        var used = new HashSet<int>(All().Select(x => x.n));
        int n = 2; while (used.Contains(n)) n++;
        var a = new AccountInfo { n = n, color = Palette[(n - 1) % Palette.Length], icon = "tint" };
        Apply(a);
        return a;
    }

    public static bool ClaudeRunningFor(int n)
    {
        var root = Path.Combine(Data.AppData, "Claude-" + n);
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name = 'claude.exe'"))
                foreach (System.Management.ManagementObject o in q.Get())
                    using (o)
                    {
                        var cmd = o["CommandLine"] as string ?? "";
                        if (cmd.IndexOf(root + "\"", StringComparison.OrdinalIgnoreCase) >= 0 || cmd.EndsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                    }
        }
        catch { }
        return false;
    }

    // Quit the Claude windows of account n (all its processes), waiting until they're gone.
    public static void CloseClaudeFor(int n)
    {
        var root = Path.Combine(Data.AppData, "Claude-" + n);
        var pids = new List<int>();
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'claude.exe'"))
                foreach (System.Management.ManagementObject o in q.Get())
                    using (o)
                    {
                        var cmd = o["CommandLine"] as string ?? "";
                        if (cmd.IndexOf(root + "\"", StringComparison.OrdinalIgnoreCase) >= 0 || cmd.EndsWith(root, StringComparison.OrdinalIgnoreCase))
                            pids.Add(Convert.ToInt32(o["ProcessId"]));
                    }
        }
        catch { }
        foreach (var id in pids) try { Process.GetProcessById(id).Kill(); } catch { }
        foreach (var id in pids) try { Process.GetProcessById(id).WaitForExit(5000); } catch { }
        if (pids.Count > 0) System.Threading.Thread.Sleep(500);   // let it release its files
    }

    // Remove account n's launcher and shortcuts; optionally its sign-in too.
    public static void Remove(AccountInfo a, bool removeSignIn)
    {
        if (a.n == 1) throw new Exception("Account 1 is the normal Claude app and can't be removed here.");
        CloseClaudeFor(a.n);
        foreach (var lnk in ShortcutsOf(a).ToList()) try { File.Delete(lnk); } catch { }
        var dir = AccountDir(a.n);
        if (Directory.Exists(dir))
        {
            foreach (var p in Process.GetProcesses())
                try { if (p.MainModule.FileName.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
        if (removeSignIn)
            foreach (var f in Data.FoldersNamed("Claude-" + a.n)) try { Directory.Delete(f, true); } catch { }
        var s = Load();
        s.accounts.RemoveAll(x => x.n == a.n);
        Save(s);
        RefreshShellIcons();
    }

    // ---------- first-time setup (run by the installer) ----------
    public static void Setup(int count, bool widget)
    {
        var existing = All();
        for (int n = 1; n <= count; n++)
        {
            var a = existing.FirstOrDefault(x => x.n == n) ?? new AccountInfo { n = n };
            Apply(a);
        }
        var self = Path.Combine(TrackerDir, "Claude Tracker.exe");
        Directory.CreateDirectory(StartMenuDir);
        MakeShortcut(Path.Combine(StartMenuDir, "Manage Claude accounts.lnk"), self, "--manage", self, TrackerDir, "Add accounts, change icons and names");
        MakeShortcut(Path.Combine(StartMenuDir, "Claude Tracker.lnk"), self, null, self, TrackerDir, "Plan limits and conversations for all your Claude accounts");
        Directory.CreateDirectory(StartupDir);
        var startup = Path.Combine(StartupDir, "Claude Tracker.lnk");
        if (widget) MakeShortcut(startup, self, null, self, TrackerDir, "Claude Tracker");
        else try { File.Delete(startup); } catch { }
        if (widget) RegisterLimitsPlugin();
    }

    // The limits plugin saves the plan-limit numbers each Claude Code reply carries, so the panel
    // can show them per account. It's a local Claude Code plugin; register it in ~/.claude/settings.json.
    public static void RegisterLimitsPlugin()
    {
        var plugins = Path.Combine(TrackerDir, "plugins");
        var reg = Path.Combine(plugins, @"limits-beacon\hooks\register.ts");
        if (!File.Exists(reg)) return;
        var dirForTs = Data.BySessionDir.Replace('\\', '/') + "/";
        var ts = File.ReadAllText(reg);
        ts = Regex.Replace(ts, "const DIR = '[^']*'", "const DIR = '" + dirForTs + "'");
        File.WriteAllText(reg, ts);
        Directory.CreateDirectory(Data.BySessionDir);

        var settingsPath = ClaudeSettingsPath();
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        Dictionary<string, object> s;
        try { s = File.Exists(settingsPath) ? js.DeserializeObject(File.ReadAllText(settingsPath)) as Dictionary<string, object> : new Dictionary<string, object>(); }
        catch { return; }   // never rewrite a settings file we couldn't read
        if (s == null) return;
        var mk = Data.Obj(s, "extraKnownMarketplaces") ?? new Dictionary<string, object>();
        var en = Data.Obj(s, "enabledPlugins") ?? new Dictionary<string, object>();
        var want = new Dictionary<string, object> { { "source", new Dictionary<string, object> { { "source", "directory" }, { "path", plugins } } } };
        var have = Data.Obj(Data.Obj(mk, "claude-tracker"), "source");
        bool changed = Data.Str(have, "path") != plugins || !(Data.Get(en, "limits-beacon@claude-tracker") is bool);
        if (!changed) return;
        mk["claude-tracker"] = want; en["limits-beacon@claude-tracker"] = true;
        s["extraKnownMarketplaces"] = mk; s["enabledPlugins"] = en;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
        if (File.Exists(settingsPath)) File.Copy(settingsPath, settingsPath + ".before-claude-tracker", true);
        File.WriteAllText(settingsPath, Pretty(js.Serialize(s)));
    }

    // Undo RegisterLimitsPlugin (used by the uninstaller).
    public static void UnregisterLimitsPlugin()
    {
        var settingsPath = ClaudeSettingsPath();
        if (!File.Exists(settingsPath)) return;
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        Dictionary<string, object> s;
        try { s = js.DeserializeObject(File.ReadAllText(settingsPath)) as Dictionary<string, object>; } catch { return; }
        if (s == null) return;
        bool changed = false;
        var mk = Data.Obj(s, "extraKnownMarketplaces"); if (mk != null && mk.Remove("claude-tracker")) changed = true;
        var en = Data.Obj(s, "enabledPlugins"); if (en != null && en.Remove("limits-beacon@claude-tracker")) changed = true;
        if (!changed) return;
        File.Copy(settingsPath, settingsPath + ".before-claude-tracker-removal", true);
        File.WriteAllText(settingsPath, Pretty(js.Serialize(s)));
    }

    // Indent JSON so the settings file stays readable.
    static string Pretty(string json)
    {
        var sb = new StringBuilder(); int ind = 0; bool str = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\' || (i > 1 && json[i - 2] == '\\'))) str = !str;
            if (str) { sb.Append(c); continue; }
            if ((c == '{' || c == '[') && i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c); sb.Append(json[i + 1]); i++; }
            else if (c == '{' || c == '[') { sb.Append(c); sb.Append('\n'); ind++; sb.Append(new string(' ', ind * 2)); }
            else if (c == '}' || c == ']') { sb.Append('\n'); ind--; sb.Append(new string(' ', ind * 2)); sb.Append(c); }
            else if (c == ',') { sb.Append(c); sb.Append('\n'); sb.Append(new string(' ', ind * 2)); }
            else if (c == ':') sb.Append(": ");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
