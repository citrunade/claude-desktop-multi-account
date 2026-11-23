using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// Reads Claude's own files and works out, per account: plan limits and the conversations that
// need you (working, new, waiting, cut by a limit, pinned). Everything the panel shows comes from here.
// Output is plain dictionaries and object[] arrays, the same shape the panel draws from.
static class Data
{
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string ClaudeDir = Path.Combine(Home, ".claude");
    static readonly string SessionsDir = Path.Combine(ClaudeDir, "sessions");
    static readonly string ProjectsDir = Path.Combine(ClaudeDir, "projects");
    public static readonly string BySessionDir = Path.Combine(ClaudeDir, "limits-tracker", "by-session");
    public static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static readonly string ClaudeExe = Path.Combine(Local, @"Microsoft\WindowsApps\claude-desktop.exe");
    const string SpecialOrg = "00000000-0000-4000-8000-000000000001";
    const double ScanWindow = 8 * 86400;
    const int TailBytes = 256 * 1024;
    static readonly string[] Cats = { "pinned", "working", "unseen", "waiting", "cut" };

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public static double Now() { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }
    static double ToTs(DateTime utc) { return (utc - new DateTime(1970, 1, 1)).TotalSeconds; }

    // ---------- small helpers ----------
    public static Dictionary<string, object> ReadJson(string path)
    {
        try { return Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>; }
        catch { return null; }
    }
    public static object Get(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) ? o : null; }
    public static string Str(Dictionary<string, object> d, string k) { return Get(d, k) as string; }
    public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object>; }
    public static double? Dbl(object o) { if (o == null || o is string || o is bool) return null; try { return Convert.ToDouble(o); } catch { return null; } }
    static bool Truthy(object o) { return o is bool && (bool)o; }
    static string Or(params string[] xs) { foreach (var x in xs) if (!string.IsNullOrEmpty(x)) return x; return null; }

    static double? IsoToTs(object o)
    {
        var s = o as string;
        if (s == null) return Dbl(o);
        DateTime dt;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
            return ToTs(dt);
        return null;
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    static bool PidAlive(object pidObj)
    {
        var p = Dbl(pidObj);
        if (p == null || p <= 0) return false;
        IntPtr h = OpenProcess(0x1000, false, (int)p.Value);
        if (h == IntPtr.Zero) return false;
        uint code; bool ok = GetExitCodeProcess(h, out code);
        CloseHandle(h);
        return ok && code == 259;
    }

    static List<string> TailLines(string path, int nbytes)
    {
        try
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long size = f.Length;
                bool cut = size > nbytes;
                if (cut) f.Seek(size - nbytes, SeekOrigin.Begin);
                var buf = new byte[cut ? nbytes : size];
                int read = 0, n;
                while (read < buf.Length && (n = f.Read(buf, read, buf.Length - read)) > 0) read += n;
                var text = Encoding.UTF8.GetString(buf, 0, read);
                var lines = text.Split('\n').ToList();
                if (cut && lines.Count > 0) lines.RemoveAt(0);  // drop partial line
                return lines;
            }
        }
        catch { return new List<string>(); }
    }

    static string ReadAllShared(string path)
    {
        try
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(f, Encoding.UTF8)) return r.ReadToEnd();
        }
        catch { return null; }
    }

    static readonly Regex TitleRe = new Regex("\"customTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"");
    static readonly Regex AgentRe = new Regex("\"agentName\":\"((?:[^\"\\\\]|\\\\.)*)\"");
    static readonly Regex OrgRe = new Regex("\"ownerOrganizationUuid\":\"([0-9a-f-]+)\"");

    static string LastMatch(string text, Regex rx)
    {
        if (text == null) return null;
        Match last = null;
        foreach (Match m in rx.Matches(text)) last = m;
        return last == null ? null : last.Groups[1].Value;
    }
    static string Unescape(string s) { try { return Json.Deserialize<string>("\"" + s + "\""); } catch { return s; } }

    // Whole-file lookups are cached per file size + time, since most transcripts don't change between polls.
    class FileFacts { public long Size; public DateTime Mtime; public string Title; public string Org; public bool HaveTitle, HaveOrg; public Analysis A; }
    static readonly Dictionary<string, FileFacts> facts = new Dictionary<string, FileFacts>();

    static FileFacts FactsFor(string path, FileInfo fi)
    {
        FileFacts f;
        if (!facts.TryGetValue(path, out f) || f.Size != fi.Length || f.Mtime != fi.LastWriteTimeUtc)
        {
            f = new FileFacts { Size = fi.Length, Mtime = fi.LastWriteTimeUtc };
            facts[path] = f;
        }
        return f;
    }
    // Reading a whole transcript (often 10+ MB) is the costly part, so these are kept per file across
    // growth: the title is re-read at most once a minute, the account never changes once found.
    static readonly Dictionary<string, KeyValuePair<DateTime, string>> titleByPath = new Dictionary<string, KeyValuePair<DateTime, string>>();
    static readonly Dictionary<string, string> orgByPath = new Dictionary<string, string>();
    static string TitleFromFile(string path, FileFacts f)
    {
        if (f.HaveTitle) return f.Title;
        KeyValuePair<DateTime, string> c;
        lock (titleByPath)
            if (titleByPath.TryGetValue(path, out c) && (DateTime.UtcNow - c.Key).TotalSeconds < 60) { f.Title = c.Value; f.HaveTitle = true; return f.Title; }
        var text = ReadAllShared(path);
        string t = LastMatch(text, TitleRe) ?? LastMatch(text, AgentRe);
        f.Title = t == null ? null : Unescape(t); f.HaveTitle = true;
        lock (titleByPath) titleByPath[path] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, f.Title);
        return f.Title;
    }
    static string OrgFromFile(string path, FileFacts f)
    {
        if (f.HaveOrg) return f.Org;
        string known;
        lock (orgByPath)
            if (orgByPath.TryGetValue(path, out known)) { f.Org = known; f.HaveOrg = true; return known; }
        f.Org = LastMatch(ReadAllShared(path), OrgRe); f.HaveOrg = true;
        if (f.Org != null) lock (orgByPath) orgByPath[path] = f.Org;
        return f.Org;
    }

    // ---------- accounts ----------
    // The Store version of Claude may keep an account's data in its own private copy of AppData
    // (%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming), which other programs can't see under
    // the usual path. Look in both, and always name a folder by its usual path.
    public static List<string> RoamingRoots()
    {
        var r = new List<string> { AppData };
        try
        {
            var pk = Path.Combine(Local, "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.GetDirectories(pk, "Claude_*"))
                {
                    var lc = Path.Combine(d, @"LocalCache\Roaming");
                    if (Directory.Exists(lc)) r.Add(lc);
                }
        }
        catch { }
        return r;
    }
    // Every real folder behind a usual-path folder name like "Claude-3".
    public static List<string> FoldersNamed(string name)
    {
        return RoamingRoots().Select(r => Path.Combine(r, name)).Where(Directory.Exists).ToList();
    }

    public class Profile
    {
        public int N; public string Root; public string Launcher; public bool CanStart;
        public List<string> Folders = new List<string>();   // Root plus any private copy
    }

    static List<Profile> profCache; static DateTime profAt;
    public static List<Profile> Profiles()
    {
        if (profCache != null && (DateTime.UtcNow - profAt).TotalSeconds < 10) return profCache;
        profCache = LoadProfiles(); profAt = DateTime.UtcNow;
        return profCache;
    }
    static List<Profile> LoadProfiles()
    {
        var outp = new List<Profile>();
        try
        {
            var data = Path.Combine(Local, "Claude-Data");
            if (Directory.Exists(data))
                foreach (var root in Directory.GetDirectories(data, "claude_*"))
                    outp.Add(new Profile { N = 1, Root = root, Launcher = Accounts.LauncherPath(1), CanStart = File.Exists(ClaudeExe), Folders = { root } });
        }
        catch { }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rr in RoamingRoots())
            try { foreach (var d in Directory.GetDirectories(rr, "Claude-*")) names.Add(Path.GetFileName(d)); } catch { }
        foreach (var name in names)
        {
            var m = Regex.Match(name, @"^Claude-(\d+)$");
            if (!m.Success) continue;
            int n = int.Parse(m.Groups[1].Value);
            var exe = Accounts.LauncherPath(n);
            outp.Add(new Profile { N = n, Root = Path.Combine(AppData, name), Launcher = exe, CanStart = exe != null, Folders = FoldersNamed(name) });
        }
        return outp.OrderBy(p => p.N).ToList();
    }

    static HashSet<string> ProfileOrgs(string root)
    {
        var orgs = new HashSet<string>();
        try
        {
            var ccs = Path.Combine(root, "claude-code-sessions");
            if (!Directory.Exists(ccs)) return orgs;
            foreach (var d1 in Directory.GetDirectories(ccs))
                foreach (var d2 in Directory.GetDirectories(d1))
                {
                    var b = Path.GetFileName(d2);
                    if (Regex.IsMatch(b, "^[0-9a-f-]{36}$") && b != SpecialOrg) orgs.Add(b);
                }
        }
        catch { }
        return orgs;
    }

    // ---------- sessions ----------
    static Dictionary<string, Dictionary<string, object>> LiveSessions()
    {
        var outp = new Dictionary<string, Dictionary<string, object>>();
        try
        {
            if (!Directory.Exists(SessionsDir)) return outp;
            foreach (var path in Directory.GetFiles(SessionsDir, "*.json"))
            {
                var d = ReadJson(path);
                var sid = Str(d, "sessionId");
                if (sid == null) continue;
                d["alive"] = PidAlive(Get(d, "pid"));
                Dictionary<string, object> prev;
                if (!outp.TryGetValue(sid, out prev)
                    || (Truthy(d["alive"]) && !Truthy(prev["alive"]))
                    || (Dbl(Get(d, "updatedAt")) ?? 0) > (Dbl(Get(prev, "updatedAt")) ?? 0))
                    outp[sid] = d;
            }
        }
        catch { }
        return outp;
    }

    // cli sessionId -> the desktop app's own record (title, archived, last focus, turn end, which account folder)
    public static Dictionary<string, Dictionary<string, object>> AppRecords()
    {
        var roots = new List<string>();
        try
        {
            var data = Path.Combine(Local, "Claude-Data");
            if (Directory.Exists(data))
                foreach (var d in Directory.GetDirectories(data)) roots.Add(Path.Combine(d, "claude-code-sessions"));
        }
        catch { }
        var usual = new Dictionary<string, string>();   // sessions folder -> its account folder by usual path
        foreach (var rr in RoamingRoots())
            try
            {
                foreach (var d in Directory.GetDirectories(rr, "Claude*"))
                {
                    var ccs = Path.Combine(d, "claude-code-sessions");
                    roots.Add(ccs);
                    usual[ccs] = Path.Combine(AppData, Path.GetFileName(d));
                }
            }
            catch { }

        var outp = new Dictionary<string, Dictionary<string, object>>();
        lock (recCache)
        {
            var seenPaths = new HashSet<string>();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var d1 in Directory.GetDirectories(root))
                        foreach (var d2 in Directory.GetDirectories(d1))
                            foreach (var path in Directory.GetFiles(d2, "local_*.json"))
                            {
                                seenPaths.Add(path);
                                var d = CachedRecord(path, Path.GetFileName(d2), usual.ContainsKey(root) ? usual[root] : Path.GetDirectoryName(root));
                                if (d == null) continue;
                                foreach (var key in new[] { Str(d, "cliSessionId"), Str(d, "lastAssistantCliSessionId") })
                                {
                                    if (key == null) continue;
                                    Dictionary<string, object> prev;
                                    if (!outp.TryGetValue(key, out prev) || (Dbl(Get(d, "lastActivityAt")) ?? 0) > (Dbl(Get(prev, "lastActivityAt")) ?? 0))
                                        outp[key] = d;
                                }
                            }
                }
                catch { }
            }
            foreach (var k in recCache.Keys.Where(k => !seenPaths.Contains(k)).ToList()) recCache.Remove(k);
        }
        return outp;
    }

    // The app's record files are large (mostly tool lists) and there are hundreds of them; only the few
    // that changed since the last refresh are read again, and only the fields used here are kept.
    class RecEntry { public long Size; public DateTime Mtime; public Dictionary<string, object> D; }
    static readonly Dictionary<string, RecEntry> recCache = new Dictionary<string, RecEntry>();
    static readonly string[] RecKeys = { "sessionId", "cliSessionId", "lastAssistantCliSessionId", "cwd", "title", "isArchived",
        "lastActivityAt", "lastFocusedAt", "lastTurnEdgeAt", "postTurnSummary" };
    static Dictionary<string, object> CachedRecord(string path, string org, string root)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return null; } catch { return null; }
        RecEntry e;
        if (recCache.TryGetValue(path, out e) && e.Size == fi.Length && e.Mtime == fi.LastWriteTimeUtc) return e.D;
        var full = ReadJson(path);
        if (full == null) return null;   // maybe caught mid-write: try again next time
        var slim = new Dictionary<string, object>();
        foreach (var k in RecKeys) { object v; if (full.TryGetValue(k, out v)) slim[k] = v; }
        slim["_org"] = org;
        slim["_root"] = root;
        recCache[path] = new RecEntry { Size = fi.Length, Mtime = fi.LastWriteTimeUtc, D = slim };
        return slim;
    }

    // ---------- idle helpers ----------
    // Every open conversation keeps a background Claude process (~100 MB) until the app quits, even
    // when nothing has happened in it for days. Once a minute, close the ones idle past the panel's
    // hide time: not working, not waiting on a permission, not pinned, not just looked at.
    static DateTime idleCheckedAt;
    static void CloseIdleHelpers(Dictionary<string, Dictionary<string, object>> live, Marks marks, AccountSettings settings, HashSet<string> withJobs)
    {
        if (!settings.closeIdleHelpers || (DateTime.UtcNow - idleCheckedAt).TotalSeconds < 60) return;
        idleCheckedAt = DateTime.UtcNow;
        double nowMs = Now() * 1000, limitMs = Math.Max(15, settings.hideAfterMinutes) * 60000.0;
        foreach (var info in live.Values)
        {
            try
            {
                if (!Truthy(Get(info, "alive")) || Str(info, "status") != "idle") continue;
                var sid = Str(info, "sessionId");
                if (sid == null || marks.IsPinned(sid) || withJobs.Contains(sid)) continue;
                double last = Math.Max(Dbl(Get(info, "updatedAt")) ?? 0, Dbl(Get(info, "statusUpdatedAt")) ?? 0);
                double seen; lock (SeenLock) { LoadSeen(); if (seenAt.TryGetValue(sid, out seen)) last = Math.Max(last, seen); }
                if (last <= 0 || nowMs - last < limitMs) continue;
                int pid = (int)(Dbl(Get(info, "pid")) ?? 0);
                if (pid <= 0) continue;
                using (var p = Process.GetProcessById(pid))
                {
                    // Make sure the number still belongs to that same helper, not a newer process.
                    if (!string.Equals(p.ProcessName, "claude", StringComparison.OrdinalIgnoreCase)) continue;
                    var started = Str(info, "procStart");
                    if (started != null && p.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() != started) continue;
                    p.Kill();
                }
            }
            catch { }
        }
    }

    // ---------- seen ----------
    // The app only notes that you looked at a conversation when you switch to it, so one you were
    // already watching when it finished stays "new". Fill that in here: in each account, the
    // conversation focused last is the one on screen, and while that account's window is in front
    // it counts as seen. Opening a conversation from the panel counts too. Times are milliseconds.
    static string SeenFile { get { return Path.Combine(Accounts.TrackerDir, "seen.json"); } }
    static readonly object SeenLock = new object();
    static Dictionary<string, double> seenAt;
    static bool seenDirty;

    static void LoadSeen()
    {
        if (seenAt != null) return;
        seenAt = new Dictionary<string, double>();
        var d = ReadJson(SeenFile);
        if (d != null) foreach (var kv in d) { var v = Dbl(kv.Value); if (v != null) seenAt[kv.Key] = v.Value; }
    }
    static void SaveSeen()
    {
        if (!seenDirty) return;
        double keepFrom = Now() * 1000 - ScanWindow * 1000;
        foreach (var k in seenAt.Where(kv => kv.Value < keepFrom).Select(kv => kv.Key).ToList()) seenAt.Remove(k);
        try
        {
            var tmp = SeenFile + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(seenAt));
            if (File.Exists(SeenFile)) File.Replace(tmp, SeenFile, null); else File.Move(tmp, SeenFile);
            seenDirty = false;
        }
        catch { }
    }
    public static void MarkSeen(string sid)
    {
        if (sid == null) return;
        lock (SeenLock) { LoadSeen(); seenAt[sid] = Now() * 1000; seenDirty = true; SaveSeen(); }
    }
    static double LastSeen(Dictionary<string, object> rec, string sid)
    {
        double t = Dbl(Get(rec, "lastFocusedAt")) ?? 0, s;
        lock (SeenLock)
        {
            LoadSeen();
            foreach (var k in new[] { sid, Str(rec, "cliSessionId"), Str(rec, "lastAssistantCliSessionId") })
                if (k != null && seenAt.TryGetValue(k, out s) && s > t) t = s;
        }
        return t;
    }

    // The conversation each account's window shows (the one focused last there), by transcript id;
    // OnScreenFront is the one in the window that is in front, if a Claude window is.
    public static volatile HashSet<string> OnScreen = new HashSet<string>(), OnScreenFront = new HashSet<string>();

    static void NoteOnScreen(Dictionary<string, Dictionary<string, object>> recs)
    {
        var root = ForegroundRoot();
        var tops = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in recs.Values)
        {
            var rr = Str(rec, "_root"); if (rr == null || Truthy(Get(rec, "isArchived"))) continue;
            Dictionary<string, object> cur;
            if (!tops.TryGetValue(rr, out cur) || (Dbl(Get(rec, "lastFocusedAt")) ?? 0) > (Dbl(Get(cur, "lastFocusedAt")) ?? 0)) tops[rr] = rec;
        }
        var on = new HashSet<string>(); var front = new HashSet<string>();
        foreach (var kv in tops)
            foreach (var k in new[] { Str(kv.Value, "cliSessionId"), Str(kv.Value, "lastAssistantCliSessionId") })
                if (k != null) { on.Add(k); if (root != null && string.Equals(kv.Key, root, StringComparison.OrdinalIgnoreCase)) front.Add(k); }
        OnScreen = on; OnScreenFront = front;
        if (root == null) return;
        Dictionary<string, object> top = null;
        foreach (var rec in recs.Values)
            if (string.Equals(Str(rec, "_root"), root, StringComparison.OrdinalIgnoreCase)
                && (top == null || (Dbl(Get(rec, "lastFocusedAt")) ?? 0) > (Dbl(Get(top, "lastFocusedAt")) ?? 0)))
                top = rec;
        if (top == null) return;
        double now = Now() * 1000;
        lock (SeenLock)
        {
            LoadSeen();
            foreach (var k in new[] { Str(top, "cliSessionId"), Str(top, "lastAssistantCliSessionId") })
                if (k != null) { seenAt[k] = now; seenDirty = true; }
            // Write at most once a minute while you sit on a conversation.
            if (seenDirty && (DateTime.UtcNow - seenSavedAt).TotalSeconds > 60) { SaveSeen(); seenSavedAt = DateTime.UtcNow; }
        }
    }
    static DateTime seenSavedAt;

    // Which account's Claude window is in front (its account folder), or null. The answer is kept
    // per window process, so the slow process lookup runs only when the front window changes.
    static int fgPid = -1; static string fgRoot;
    static string ForegroundRoot()
    {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        if (pid == 0) return null;
        if ((int)pid == fgPid) return fgRoot;
        string root = null;
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("SELECT Name, CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                foreach (System.Management.ManagementObject o in q.Get())
                    using (o)
                    {
                        if (!string.Equals(o["Name"] as string, "claude.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var cmd = o["CommandLine"] as string ?? "";
                        var profs = Profiles();
                        var p = profs.FirstOrDefault(x => x.N != 1 && cmd.IndexOf(x.Root, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (p == null && cmd.IndexOf("--user-data-dir", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            var ones = profs.Where(x => x.N == 1).ToList();
                            if (ones.Count == 1) p = ones[0];
                        }
                        if (p != null) root = p.Root;
                    }
        }
        catch { }
        fgPid = (int)pid; fgRoot = root;
        return root;
    }

    class Analysis { public double? LastTs, ResetAt, Ctx; public string LastKind, LimitKind, Title, Org, Model; public bool KeepClock; public List<Job> Jobs; }

    // ---------- background jobs ----------
    // A conversation can answer and end its turn while something it started keeps going (a command
    // run in the background, a helper agent, a watcher). Claude then calls it idle, but it will wake
    // up by itself when the job ends. The transcript says when each job starts and when it ends.
    class Job { public string Id, Kind; public double Ts; public double? Expires; }
    static readonly Regex JobStop = new Regex("\"name\":\"TaskStop\",\"input\":\\{\"task_id\":\"([a-z0-9]+)\"");
    // Starts are read only from the structured result Claude saves with the tool call, never from
    // text: a command's printed output can contain the very same words.
    static readonly Regex JobEnd = new Regex("\"(?:content|prompt)\":\"<task-notification>\\\\n<task-id>([a-z0-9]+)</task-id>(?:[^\"\\\\]|\\\\.)*?<status>[a-z]+</status>");

    static List<Job> FindJobs(List<string> lines)
    {
        var open = new Dictionary<string, Job>();
        foreach (var line in lines)
        {
            if (!line.StartsWith("{")) continue;
            if (line.Contains("<task-notification>"))
                foreach (Match m in JobEnd.Matches(line)) open.Remove(m.Groups[1].Value);
            if (line.Contains("\"TaskStop\"") && line.Contains("\"type\":\"assistant\""))
                foreach (Match m in JobStop.Matches(line)) open.Remove(m.Groups[1].Value);
            if (!line.Contains("\"toolUseResult\"") || !line.Contains("\"type\":\"user\"")) continue;
            if (!line.Contains("\"backgroundTaskId\"") && !line.Contains("\"async_launched\"") && !line.Contains("\"timeoutMs\"")) continue;
            Dictionary<string, object> e;
            try { e = Json.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
            if (e == null || Str(e, "type") != "user" || Truthy(Get(e, "isSidechain"))) continue;
            var r = Obj(e, "toolUseResult");
            if (r == null) continue;
            double ts = IsoToTs(Get(e, "timestamp")) ?? 0;
            string id = null, kind = null; double? expires = null;
            if (Str(r, "backgroundTaskId") != null) { id = Str(r, "backgroundTaskId"); kind = "shell"; }
            else if (Str(r, "status") == "async_launched" && Str(r, "agentId") != null)
            {
                // A helper agent; one that never reports back stops counting after two hours.
                id = Str(r, "agentId"); kind = "agent"; expires = ts + 2 * 3600;
            }
            else if (Str(r, "taskId") != null && Dbl(Get(r, "timeoutMs")) != null)
            {
                id = Str(r, "taskId"); kind = "watch";
                if (!Truthy(Get(r, "persistent"))) expires = ts + Dbl(Get(r, "timeoutMs")).Value / 1000 + 30;
            }
            if (id != null && !open.ContainsKey(id)) open[id] = new Job { Id = id, Kind = kind, Ts = ts, Expires = expires };
        }
        return open.Values.ToList();
    }

    // Jobs still running: started by the Claude process that is alive now (a restart kills the old
    // ones without a word), not ended, and for a watcher not past its expiry.
    // The end of a background command isn't always written down, so one only counts while that
    // Claude process still has a command window (a shell) running under it.
    static int RunningJobs(Analysis a, Dictionary<string, object> info, double now)
    {
        if (a.Jobs == null || a.Jobs.Count == 0 || !Truthy(Get(info, "alive"))) return 0;
        double started = (Dbl(Get(info, "startedAt")) ?? 0) / 1000;
        var live = a.Jobs.Where(j => j.Ts >= started - 5 && (j.Expires == null || j.Expires > now)).ToList();
        if (live.Any(j => j.Kind == "shell") && !HasShellChild((int)(Dbl(Get(info, "pid")) ?? 0)))
            live.RemoveAll(j => j.Kind == "shell");
        return live.Count;
    }

    static readonly HashSet<string> ShellNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bash.exe", "sh.exe", "powershell.exe", "pwsh.exe", "cmd.exe" };
    static Dictionary<int, List<string>> childNames = new Dictionary<int, List<string>>();
    static DateTime childrenAt;
    static bool HasShellChild(int pid)
    {
        if (pid <= 0) return false;
        // One look at the process list every few seconds, and only when some chat needs it.
        if ((DateTime.UtcNow - childrenAt).TotalSeconds > 4)
        {
            var map = new Dictionary<int, List<string>>();
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT ParentProcessId, Name FROM Win32_Process"))
                    foreach (System.Management.ManagementObject o in q.Get())
                        using (o)
                        {
                            int parent = Convert.ToInt32(o["ParentProcessId"]);
                            List<string> l; if (!map.TryGetValue(parent, out l)) map[parent] = l = new List<string>();
                            l.Add(o["Name"] as string ?? "");
                        }
            }
            catch { }
            childNames = map; childrenAt = DateTime.UtcNow;
        }
        List<string> kids;
        return childNames.TryGetValue(pid, out kids) && kids.Any(n => ShellNames.Contains(n));
    }

    static Analysis Analyse(string path, FileFacts f)
    {
        if (f.A != null) return f.A;
        var a = new Analysis();
        var lines = TailLines(path, TailBytes);
        // A chat the panel opened only to start an account's 5-hour clock is small and carries a marker.
        a.KeepClock = f.Size < 128 * 1024 && lines.Any(l => l.Contains(ClaudeTray.KeepClockMarker));
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.StartsWith("{")) continue;
            Dictionary<string, object> e;
            try { e = Json.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
            if (e == null) continue;
            string type = Str(e, "type");
            var msg = Obj(e, "message");
            if (a.Ctx == null && type == "assistant" && !Truthy(Get(e, "isSidechain")) && !Truthy(Get(e, "isApiErrorMessage")))
            {
                var u = Obj(msg, "usage");
                double tot = (Dbl(Get(u, "input_tokens")) ?? 0) + (Dbl(Get(u, "cache_creation_input_tokens")) ?? 0) + (Dbl(Get(u, "cache_read_input_tokens")) ?? 0);
                if (tot > 0) { a.Ctx = tot; a.Model = Str(msg, "model"); }
            }
            if (a.Title == null && type == "custom-title") a.Title = Str(e, "customTitle");
            var ts = IsoToTs(Get(e, "timestamp"));
            if (a.LastTs == null && ts != null) a.LastTs = ts;
            if (a.LastKind == null && (type == "assistant" || type == "user") && !Truthy(Get(e, "isSidechain")))
            {
                if (Truthy(Get(e, "isMeta"))) continue;
                if (type == "assistant")
                {
                    if (Truthy(Get(e, "isApiErrorMessage")))
                    {
                        var q = Obj(e, "quotaLimits");
                        if (Str(e, "error") == "rate_limit" || Str(q, "status") == "rejected")
                        {
                            a.LastKind = "limit";
                            a.ResetAt = Dbl(Get(q, "resetsAt"));
                            a.LimitKind = Str(q, "rateLimitType") ?? "five_hour";
                        }
                        else a.LastKind = "error";
                    }
                    else a.LastKind = "assistant";
                }
                else
                {
                    var content = Get(msg, "content") as object[];
                    bool toolResult = content != null && content.Any(c => c is Dictionary<string, object> && Str((Dictionary<string, object>)c, "type") == "tool_result");
                    a.LastKind = toolResult ? "tool" : "user";
                }
            }
            if (a.LastKind != null && a.LastTs != null && a.Title != null && a.Ctx != null) break;
        }
        if (a.LastKind == "limit") a.Org = OrgFromFile(path, f);
        a.Jobs = FindJobs(lines);
        f.A = a;
        return a;
    }

    class Row { public string Sid, Path; public double Mtime; public Dictionary<string, object> Info; public Analysis A; public FileFacts F; public string Org; public bool OrgDone; }

    // sessionId -> (link, account folder), refreshed with every State(), so a click opens at once.
    static Dictionary<string, KeyValuePair<string, string>> openIndex = new Dictionary<string, KeyValuePair<string, string>>();

    // ---------- the whole picture ----------
    public static Dictionary<string, object> State()
    {
        var index = new Dictionary<string, KeyValuePair<string, string>>();
        double now = Now();
        var live = LiveSessions();
        var recs = AppRecords();
        NoteOnScreen(recs);
        var profs = Profiles();
        var marks = Marks.Load();
        var settings = Accounts.Load();
        double ActiveWindow = Math.Max(5, settings.hideAfterMinutes) * 60.0;
        double scanFor = Math.Max(ScanWindow, ActiveWindow + 86400);

        // org -> profile. An org seen in a dedicated app belongs to that app.
        var owner = new Dictionary<string, Profile>();
        foreach (var p in profs.OrderBy(p => p.N == 1).ThenBy(p => p.N))
            foreach (var folder in p.Folders)
                foreach (var org in ProfileOrgs(folder))
                    if (!owner.ContainsKey(org)) owner[org] = p;

        var rows = new List<Row>();
        var seen = new HashSet<string>();
        if (Directory.Exists(ProjectsDir))
            foreach (var dir in SafeDirs(ProjectsDir))
                foreach (var path in SafeFiles(dir, "*.jsonl"))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(path); if (!fi.Exists) continue; } catch { continue; }
                    seen.Add(path);
                    double mtime = ToTs(fi.LastWriteTimeUtc);
                    string sid = System.IO.Path.GetFileNameWithoutExtension(path);
                    Dictionary<string, object> info; live.TryGetValue(sid, out info);
                    bool alive = info != null && Truthy(info["alive"]);
                    if (now - mtime > scanFor && !alive && !marks.IsPinned(sid)) continue;
                    var f = FactsFor(path, fi);
                    rows.Add(new Row { Sid = sid, Path = path, Mtime = mtime, Info = info, F = f, A = Analyse(path, f) });
                }
        foreach (var k in facts.Keys.Where(k => !seen.Contains(k)).ToList()) facts.Remove(k);
        // Closing an idle helper would kill a job it still has running in the background.
        CloseIdleHelpers(live, marks, settings, new HashSet<string>(rows.Where(r => r.Info != null && RunningJobs(r.A, r.Info, now) > 0).Select(r => r.Sid)));

        Func<Row, string> orgOf = r =>
        {
            Dictionary<string, object> rec; recs.TryGetValue(r.Sid, out rec);
            var ro = Str(rec, "_org");
            if (ro != null && ro != SpecialOrg) return ro;
            if (!r.OrgDone) { r.Org = r.A.Org ?? OrgFromFile(r.Path, r.F); r.OrgDone = true; }
            return r.Org;
        };

        var five = rows.Where(r => r.A.LastKind == "limit" && r.A.ResetAt != null && r.A.LimitKind == "five_hour").ToList();
        double? latestReset = five.Count > 0 ? five.Max(r => r.A.ResetAt) : null;
        var rejections = new Dictionary<string, Dictionary<string, double>>();
        foreach (var r in rows)
            if (r.A.LastKind == "limit" && r.A.ResetAt != null && r.A.ResetAt > now)
            {
                var org = orgOf(r) ?? "unknown";
                Dictionary<string, double> k;
                if (!rejections.TryGetValue(org, out k)) rejections[org] = k = new Dictionary<string, double>();
                double prev; k.TryGetValue(r.A.LimitKind, out prev);
                k[r.A.LimitKind] = Math.Max(prev, r.A.ResetAt.Value);
            }

        var buckets = new Dictionary<string, Dictionary<string, List<Dictionary<string, object>>>>();
        Func<Dictionary<string, List<Dictionary<string, object>>>> newBucket = () => Cats.ToDictionary(c => c, c => new List<Dictionary<string, object>>());
        var flat = newBucket();

        foreach (var r in rows)
        {
            var info = r.Info ?? new Dictionary<string, object>();
            Dictionary<string, object> rec; recs.TryGetValue(r.Sid, out rec);
            if (Truthy(Get(rec, "isArchived")) || r.A.KeepClock) continue;
            bool alive = Truthy(Get(info, "alive"));
            bool busy = alive && Str(info, "status") == "busy";
            bool background = !busy && RunningJobs(r.A, info, now) > 0;
            double last = r.A.LastTs ?? r.Mtime;
            string cat = null;
            if (busy || background) cat = "working";
            else if (five.Contains(r) && r.A.ResetAt == latestReset) cat = "cut";
            else if (now - last < ActiveWindow && (r.A.LastKind == "assistant" || r.A.LastKind == "error" || r.A.LastKind == "limit")) cat = "waiting";

            // Finished stays hidden until the conversation gets new activity after it was finished.
            double finished = marks.FinishedAt(r.Sid);
            if (finished > 0 && last <= finished + 5 && cat != "working") continue;
            // Answered, but a job it started is still running: listed with working, drawn differently.
            string stateCat = background ? "background" : cat;
            if (marks.IsPinned(r.Sid)) cat = "pinned";
            if (cat == null) continue;

            var summary = Obj(rec, "postTurnSummary");
            if (stateCat == "waiting" && rec != null && LastSeen(rec, r.Sid) < (Dbl(Get(rec, "lastTurnEdgeAt")) ?? 0))
            {
                stateCat = "unseen";
                if (cat != "pinned") cat = "unseen";
            }
            string title = Or(Str(rec, "title"), Str(info, "name"), r.A.Title) ?? TitleFromFile(r.Path, r.F) ?? "Untitled conversation";
            string host = Or(Str(rec, "sessionId"), Str(info, "hostSessionId"));
            string cwd = Or(Str(rec, "cwd"), Str(info, "cwd"));
            string note = r.A.LastKind == "error" ? "stopped on an error" : r.A.LastKind == "limit" ? (r.A.LimitKind == "seven_day" ? "hit the weekly limit" : "hit a limit") : null;
            double since = (Dbl(Get(info, "statusUpdatedAt")) ?? 0) / 1000;
            var item = new Dictionary<string, object>
            {
                { "title", title },
                { "sessionId", r.Sid },
                { "state", stateCat ?? "idle" },
                { "cwd", cwd },
                { "transcript", r.Path },
                { "folder", string.IsNullOrEmpty(cwd) ? null : System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/')) },
                { "lastActivity", last },
                { "since", since > 0 ? since : last },
                { "open", host != null ? "claude://claude.ai/epitaxy/" + host : null },
                { "ctx", r.A.Ctx },
                { "running", alive },
                { "detail", Or(Str(summary, "needs_action"), Str(summary, "status_detail")) },
                { "note", note },
                { "limited", r.A.LastKind == "limit" },
            };
            if (host != null && Str(rec, "_root") != null)
                index[r.Sid] = new KeyValuePair<string, string>("claude://claude.ai/epitaxy/" + host, Str(rec, "_root"));
            var o = orgOf(r) ?? "unknown";
            Dictionary<string, List<Dictionary<string, object>>> b;
            if (!buckets.TryGetValue(o, out b)) buckets[o] = b = newBucket();
            b[cat].Add(item);
            flat[cat].Add(item);
        }

        // Latest limit readings: from the limits plugin (live) and the app's own history.
        var liveReadings = new Dictionary<string, KeyValuePair<double, object[]>>();
        if (Directory.Exists(BySessionDir))
            foreach (var path in SafeFiles(BySessionDir, "*.json"))
            {
                try { if (now - ToTs(File.GetLastWriteTimeUtc(path)) > ScanWindow) { File.Delete(path); continue; } } catch { continue; }
                var d = ReadJson(path);
                if (d == null) continue;
                Dictionary<string, object> rec; recs.TryGetValue(Str(d, "sessionId") ?? "", out rec);
                var org = Str(rec, "_org");
                if (org == null || org == SpecialOrg) continue;
                var atRaw = Dbl(Get(d, "at"));
                double at = atRaw != null ? atRaw.Value / 1000 : ToTs(File.GetLastWriteTimeUtc(path));
                if (!liveReadings.ContainsKey(org) || at > liveReadings[org].Key)
                    liveReadings[org] = new KeyValuePair<double, object[]>(at, Get(d, "rateLimits") as object[] ?? new object[0]);
            }
        var samples = AppSamples(profs);

        var accounts = new List<Dictionary<string, object>>();
        var orgs = new HashSet<string>(owner.Keys.Concat(buckets.Keys).Concat(liveReadings.Keys));
        orgs.Remove("unknown");
        foreach (var org in orgs)
        {
            Profile p; owner.TryGetValue(org, out p);
            double? asof; string source;
            Dictionary<string, double> rej; rejections.TryGetValue(org, out rej);
            var wins = ReadingsFor(org, liveReadings, samples, rej, now, out asof, out source);
            Dictionary<string, List<Dictionary<string, object>>> b;
            if (!buckets.TryGetValue(org, out b)) b = newBucket();
            var acct = new Dictionary<string, object>
            {
                { "n", p != null ? p.N : 99 },
                { "name", p != null ? Accounts.NameOf(settings, p.N) : "Account " + org.Substring(0, 8) },
                { "org", org }, { "asOf", asof }, { "source", source }, { "windows", wins },
                { "canStart", p != null && p.CanStart },
                { "cutResetsAt", rej != null && rej.ContainsKey("five_hour") ? (object)rej["five_hour"] : null },
            };
            foreach (var c in Cats) acct[c] = b[c].OrderByDescending(x => (double)x["lastActivity"]).Cast<object>().ToArray();
            accounts.Add(acct);
        }
        // Accounts set up here but with no Claude Code activity yet still get a card (and a + New button).
        foreach (var p in profs)
            if (!accounts.Any(a => (int)a["n"] == p.N))
            {
                var acct = new Dictionary<string, object>
                {
                    { "n", p.N }, { "name", Accounts.NameOf(settings, p.N) }, { "org", null }, { "asOf", null }, { "source", null },
                    { "windows", new object[0] }, { "canStart", p.CanStart }, { "cutResetsAt", null },
                };
                foreach (var c in Cats) acct[c] = new object[0];
                accounts.Add(acct);
            }
        if (buckets.ContainsKey("unknown"))
        {
            var acct = new Dictionary<string, object>
            {
                { "n", 100 }, { "name", "Other" }, { "org", null }, { "asOf", null }, { "source", null },
                { "windows", new object[0] }, { "canStart", false }, { "cutResetsAt", null },
            };
            foreach (var c in Cats) acct[c] = buckets["unknown"][c].OrderByDescending(x => (double)x["lastActivity"]).Cast<object>().ToArray();
            accounts.Add(acct);
        }
        accounts = accounts.OrderBy(a => (int)a["n"]).ToList();
        var newest = accounts.Where(a => (string)a["source"] == "live").OrderByDescending(a => Dbl(a["asOf"]) ?? 0).FirstOrDefault();
        if (newest != null) newest["current"] = true;

        openIndex = index;
        var result = new Dictionary<string, object> { { "now", now }, { "accounts", accounts.Cast<object>().ToArray() }, { "cutResetsAt", latestReset } };
        foreach (var c in Cats) result[c] = flat[c].OrderByDescending(x => (double)x["lastActivity"]).Cast<object>().ToArray();
        return result;
    }

    static IEnumerable<string> SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return new string[0]; } }
    static IEnumerable<string> SafeFiles(string d, string pat) { try { return Directory.GetFiles(d, pat); } catch { return new string[0]; } }

    class Sample { public double At; public double? Five, Week; }
    static Dictionary<string, Sample> AppSamples(List<Profile> profs)
    {
        var outp = new Dictionary<string, Sample>();
        foreach (var p in profs)
        foreach (var folder in p.Folders)
        {
            var d = ReadJson(Path.Combine(folder, "plan-usage-history.json"));
            var arr = Get(d, "samples") as object[];
            if (arr == null) continue;
            foreach (Dictionary<string, object> s in arr.OfType<Dictionary<string, object>>())
            {
                var org = Str(s, "org");
                var t = Dbl(Get(s, "t"));
                if (org == null || t == null) continue;
                var u = Obj(s, "u");
                Sample prev;
                if (!outp.TryGetValue(org, out prev) || t.Value / 1000 > prev.At)
                    outp[org] = new Sample { At = t.Value / 1000, Five = Dbl(Get(u, "fh")), Week = Dbl(Get(u, "sd")) };
            }
        }
        return outp;
    }

    static object[] ReadingsFor(string org, Dictionary<string, KeyValuePair<double, object[]>> live, Dictionary<string, Sample> samples,
        Dictionary<string, double> rejections, double now, out double? asof, out string source)
    {
        asof = null; source = null;
        var rd = new Dictionary<string, KeyValuePair<double?, double?>>();
        if (live.ContainsKey(org))
        {
            asof = live[org].Key; source = "live";
            foreach (var r in live[org].Value.OfType<Dictionary<string, object>>())
            {
                var kind = Str(r, "kind");
                if (kind != null) rd[kind] = new KeyValuePair<double?, double?>(Dbl(Get(r, "percentUsed")), IsoToTs(Get(r, "resetsAt")));
            }
        }
        Sample smp;
        if (samples.TryGetValue(org, out smp) && (asof == null || smp.At > asof))
        {
            asof = smp.At; source = "app";
            if (smp.Five != null) rd["five_hour"] = new KeyValuePair<double?, double?>(smp.Five, rd.ContainsKey("five_hour") ? rd["five_hour"].Value : null);
            if (smp.Week != null) rd["seven_day"] = new KeyValuePair<double?, double?>(smp.Week, rd.ContainsKey("seven_day") ? rd["seven_day"].Value : null);
        }
        if (rejections != null)
            foreach (var kv in rejections)
            {
                double? rs = rd.ContainsKey(kv.Key) ? rd[kv.Key].Value : null;
                if (rs == null || kv.Value >= rs) rd[kv.Key] = new KeyValuePair<double?, double?>(100, kv.Value);
            }
        var wins = new List<object>();
        foreach (var kind in new[] { "five_hour", "seven_day" })
        {
            string label = kind == "five_hour" ? "5-hour" : "Weekly";
            if (!rd.ContainsKey(kind))
            {
                wins.Add(new Dictionary<string, object> { { "kind", kind }, { "label", label }, { "percent", null }, { "resetsAt", null } });
                continue;
            }
            double? pct = rd[kind].Key, rs = rd[kind].Value;
            if (rs != null && rs <= now) { pct = 0; rs = null; }
            else if (rs == null && asof != null && now - asof > (kind == "five_hour" ? 5 * 3600 : 7 * 86400)) pct = null;
            wins.Add(new Dictionary<string, object> { { "kind", kind }, { "label", label }, { "percent", pct }, { "resetsAt", rs } });
        }
        return wins.ToArray();
    }

    // ---------- opening things in the right account ----------
    public static Profile ProfileForRoot(string root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        var want = Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant();
        return Profiles().FirstOrDefault(p => Path.GetFullPath(p.Root).TrimEnd('\\').ToLowerInvariant() == want);
    }

    static void Run(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false }); } catch { }
    }
    static string Quote(string s) { return "\"" + s.Replace("\"", "") + "\""; }

    // Bring up the account through its launcher (so its windows keep their own taskbar button) and hand the
    // link to that account's Claude, not to whichever one Windows treats as the default.
    public static bool OpenInAccount(Profile p, string url)
    {
        if (p.N == 1)
        {
            if (!File.Exists(ClaudeExe)) return false;
            // The launcher opens Claude and keeps its windows on Account 1's own taskbar icon.
            if (p.Launcher != null && !LauncherRunning(p.Launcher)) Run(p.Launcher, url != null ? Quote(url) : null);
            else Run(ClaudeExe, url != null ? Quote(url) : null);
        }
        else
        {
            bool open = p.Launcher != null && LauncherRunning(p.Launcher);
            if (p.Launcher != null && (!open || url == null)) Run(p.Launcher, null);
            if (url != null && File.Exists(ClaudeExe)) Run(ClaudeExe, "--user-data-dir=" + Quote(p.Root) + " " + Quote(url));
            if (p.Launcher == null && url == null) return false;
        }
        BringToFront(p);
        return true;
    }

    static bool LauncherRunning(string exe)
    {
        try
        {
            foreach (var pr in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                try { if (string.Equals(pr.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
        }
        catch { }
        return false;
    }

    // Claude usually comes to the front by itself; if Windows kept it behind, pull it forward.
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static int MainClaudePid(Profile p)
    {
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'claude.exe'"))
                foreach (System.Management.ManagementObject o in q.Get())
                    using (o)
                    {
                        var cmd = o["CommandLine"] as string ?? "";
                        if (cmd.Contains("--type=") || cmd.Contains("--output-format")) continue;
                        bool hasDir = cmd.IndexOf("--user-data-dir", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool mine = p.N == 1 ? !hasDir : cmd.IndexOf(p.Root, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (mine) return Convert.ToInt32(o["ProcessId"]);
                    }
        }
        catch { }
        return 0;
    }

    // The main window of account n's Claude, or zero.
    public static IntPtr AccountWindow(int n)
    {
        var p = Profiles().FirstOrDefault(x => x.N == n);
        if (p == null) return IntPtr.Zero;
        int pid = MainClaudePid(p);
        if (pid == 0) return IntPtr.Zero;
        IntPtr win = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) { win = h; return false; }
            return true;
        }, IntPtr.Zero);
        return win;
    }

    public static void Focus(IntPtr win)
    {
        var fg = GetForegroundWindow();
        if (fg == win) return;
        uint fgPid; uint fgThread = GetWindowThreadProcessId(fg, out fgPid), me = GetCurrentThreadId();
        if (IsIconic(win)) ShowWindow(win, 9);
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(win); SetForegroundWindow(win);
        AttachThreadInput(me, fgThread, false);
    }

    static void BringToFront(Profile p)
    {
        for (int i = 0; i < 12; i++)
        {
            System.Threading.Thread.Sleep(i == 0 ? 700 : 500);
            int pid = MainClaudePid(p);
            if (pid == 0) continue;
            IntPtr win = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                uint owner; GetWindowThreadProcessId(h, out owner);
                if (owner == pid && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) { win = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (win == IntPtr.Zero) continue;
            var fg = GetForegroundWindow();
            if (fg == win) return;
            uint fgPid; uint fgThread = GetWindowThreadProcessId(fg, out fgPid), me = GetCurrentThreadId();
            if (IsIconic(win)) ShowWindow(win, 9);
            AttachThreadInput(me, fgThread, true);
            BringWindowToTop(win); SetForegroundWindow(win);
            AttachThreadInput(me, fgThread, false);
            return;
        }
    }

    public static bool OpenConversation(string sid)
    {
        MarkSeen(sid);
        KeyValuePair<string, string> known;
        if (openIndex.TryGetValue(sid, out known))
        {
            var kp = ProfileForRoot(known.Value);
            if (kp != null) return OpenInAccount(kp, known.Key);
        }
        Dictionary<string, object> rec;
        if (!AppRecords().TryGetValue(sid, out rec)) return false;
        var host = Str(rec, "sessionId");
        var p = ProfileForRoot(Str(rec, "_root"));
        if (host == null || p == null) return false;
        return OpenInAccount(p, "claude://claude.ai/epitaxy/" + host);
    }

    public static bool StartConversation(int n, string prompt = null, string folder = null)
    {
        var p = Profiles().FirstOrDefault(x => x.N == n && x.CanStart);
        if (p == null) return false;
        var q = new List<string>();
        if (!string.IsNullOrEmpty(prompt)) q.Add("q=" + Uri.EscapeDataString(prompt));
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) q.Add("folder=" + Uri.EscapeDataString(folder));
        return OpenInAccount(p, "claude://code/new" + (q.Count > 0 ? "?" + string.Join("&", q) : ""));
    }

    // The temporary folder Claude makes for a chat started without a project folder.
    public static bool IsTempWorkspace(string folder)
    {
        return !string.IsNullOrEmpty(folder) && folder.Replace('/', '\\').IndexOf("\\scratch-workspaces\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Whether "Trust this workspace?" was already answered yes for this folder (or a folder above it).
    // Unknown counts as not trusted.
    public static bool IsTrustedFolder(string folder)
    {
        try
        {
            var cfg = ReadJson(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));
            var projects = Get(cfg, "projects") as Dictionary<string, object>;
            if (projects == null) return false;
            Func<string, string> Norm = s => s.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            var want = Norm(Path.GetFullPath(folder));
            foreach (var kv in projects)
            {
                var k = Norm(kv.Key);
                if ((want == k || want.StartsWith(k + "\\")) && Truthy(Get(kv.Value as Dictionary<string, object>, "hasTrustDialogAccepted"))) return true;
            }
        }
        catch { }
        return false;
    }

    // ---------- hand off ----------
    static readonly Regex NoteTags = new Regex(@"<(system-reminder|local-command[a-z-]*|command-[a-z-]+)>.*?</\1>", RegexOptions.Singleline);

    public static string HandoffText(string sid, out string cwd)
    {
        cwd = null;
        string path = null;
        foreach (var dir in SafeDirs(ProjectsDir))
        {
            var cand = Path.Combine(dir, sid + ".jsonl");
            if (File.Exists(cand)) { path = cand; break; }
        }
        if (path == null) return null;
        Dictionary<string, object> rec; AppRecords().TryGetValue(sid, out rec);
        Dictionary<string, object> info; LiveSessions().TryGetValue(sid, out info);
        var fi = new FileInfo(path);
        var f = FactsFor(path, fi);
        var a = Analyse(path, f);
        string title = Or(Str(rec, "title"), Str(info, "name"), a.Title) ?? TitleFromFile(path, f) ?? "Untitled conversation";
        cwd = Or(Str(rec, "cwd"), Str(info, "cwd")) ?? "";

        // The last few things said, so the new chat sees where it stopped without opening the file.
        var said = new List<KeyValuePair<string, string>>();
        var lines = TailLines(path, 512 * 1024);
        for (int i = lines.Count - 1; i >= 0 && said.Count < 5; i--)
        {
            if (!lines[i].StartsWith("{")) continue;
            Dictionary<string, object> e;
            try { e = Json.DeserializeObject(lines[i]) as Dictionary<string, object>; } catch { continue; }
            var type = Str(e, "type");
            if ((type != "user" && type != "assistant") || Truthy(Get(e, "isSidechain")) || Truthy(Get(e, "isMeta"))) continue;
            var content = Get(Obj(e, "message"), "content");
            string text = content as string;
            if (content is object[])
                text = string.Join("\n", ((object[])content).OfType<Dictionary<string, object>>().Where(c => Str(c, "type") == "text").Select(c => Str(c, "text")));
            text = NoteTags.Replace(text ?? "", "").Trim();
            if (text.Length == 0 || text.StartsWith("<")) continue;
            string who = type == "user" ? "Me" : "Claude";
            if (said.Count(s => s.Key == who) >= (who == "Me" ? 2 : 3)) continue;
            said.Add(new KeyValuePair<string, string>(who, who + ": " + (text.Length > 1200 ? text.Substring(0, 1200) + " [...]" : text)));
        }
        said.Reverse();
        var sb = new StringBuilder();
        sb.Append("I'm moving a conversation over from another of my Claude accounts. Please pick it up where it left off.\n\n");
        sb.Append("Conversation: " + title + "\n");
        if (cwd.Length > 0) sb.Append("Folder it was working in: " + cwd + "\n");
        sb.Append("Full transcript (one JSON message per line, newest at the bottom): " + path + "\n\n");
        sb.Append("Read the end of the transcript first (the last few hundred lines are usually enough) to see what was asked, what was done, and what is still open. Then tell me in a few lines where things stand and carry on.\n\n");
        sb.Append("The last messages were:\n\n" + string.Join("\n\n", said.Select(s => s.Value)));
        return sb.ToString();
    }
}

// Pinned and finished conversations, kept next to the program.
class Marks
{
    static readonly string File_ = Path.Combine(Accounts.TrackerDir, "marks.json");
    static readonly object Lock = new object();
    readonly Dictionary<string, object> d;
    Marks(Dictionary<string, object> d) { this.d = d ?? new Dictionary<string, object>(); }
    public static Marks Load() { return new Marks(Data.ReadJson(File_)); }

    public bool IsPinned(string sid) { return Data.Get(Data.Obj(d, sid), "pinned") != null; }
    public double FinishedAt(string sid) { return Data.Dbl(Data.Get(Data.Obj(d, sid), "finished")) ?? 0; }

    public static bool Set(string sid, string action)
    {
        lock (Lock)
        {
            var m = Data.ReadJson(File_) ?? new Dictionary<string, object>();
            double now = Data.Now();
            if (action == "pin") m[sid] = new Dictionary<string, object> { { "pinned", now } };          // pinning also clears "finished"
            else if (action == "finish") m[sid] = new Dictionary<string, object> { { "finished", now } }; // finishing always unpins
            else if (action == "unpin" || action == "unfinish") m.Remove(sid);
            else return false;
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            var tmp = File_ + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(m));
            if (File.Exists(File_)) File.Replace(tmp, File_, null); else File.Move(tmp, File_);
            return true;
        }
    }
}
