using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Opens the Claude desktop app signed in to one extra account, kept apart from the others.
//
// Each account gets its own data folder (%APPDATA%\Claude-N), so it stays signed in on its own
// and never mixes with the others. All accounts are the same Claude app, so Windows would stack
// their windows under one taskbar button; this launcher tags the account's windows with an app id
// of their own, plus the icon and relaunch command, so each account has its own button that can be
// pinned. It stays in the background while the account is open, because Claude makes a fresh window
// when it comes back from the tray.
//
// Claude Tracker compiles this file once per account (with the account's icon), replacing __NUMBER__.
// The name and taskbar group id are read from name.txt and appid.txt next to the launcher.
static class ClaudeAccount
{
    const int Number = __NUMBER__;
    static readonly string Dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
    static readonly string AppId = ReadOr("appid.txt", "ClaudeAccounts.Account" + Number);
    static readonly string DisplayName = ReadOr("name.txt", "Claude Account " + Number);
    static readonly string IconFile = ReadOr("iconfile.txt", null);

    static string ReadOr(string file, string fallback)
    {
        try { var t = File.ReadAllText(Path.Combine(Dir, file)).Trim(); return t.Length > 0 ? t : fallback; }
        catch { return fallback; }
    }

    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude-" + Number);
    static readonly string Claude = Path.Combine(Local, @"Microsoft\WindowsApps\claude-desktop.exe");
    static readonly string Self = Process.GetCurrentProcess().MainModule.FileName;

    [STAThread]
    static void Main(string[] args)
    {
        // --watch-only: Claude is already open; just keep its windows on their own taskbar button.
        bool watchOnly = args.Length > 0 && args[0] == "--watch-only";
        if (!watchOnly && !OpenAccount(args)) return;

        // One watcher is enough; later launches only ask Claude to show the window.
        bool first;
        using (var mutex = new Mutex(true, "Local\\" + AppId + ".Watcher", out first))
        {
            if (!first) return;
            Watch();
        }
    }

    // Anything passed to the launcher (for example a claude:// link) goes to this account's Claude.
    static bool OpenAccount(string[] args)
    {
        if (!File.Exists(Claude))
        {
            MessageBox.Show("The Claude desktop app is not installed.\n\nInstall it from claude.ai/download, then try again.", DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        try
        {
            // Account 1 is Claude's normal sign-in, so it gets no data folder of its own.
            var line = Number == 1 ? "" : "--user-data-dir=\"" + Profile + "\"";
            foreach (var a in args) line += " \"" + a.Replace("\"", "") + "\"";
            var info = new ProcessStartInfo(Claude, line);
            info.UseShellExecute = false;
            Process.Start(info);
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show("Could not open Claude for this account.\n\n" + e.Message, DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    static void Watch()
    {
        var tagged = new HashSet<IntPtr>();
        int pid = 0;
        DateTime lastSeen = DateTime.UtcNow;

        while (true)
        {
            if (pid == 0 || !Alive(pid))
            {
                tagged.Clear();
                pid = FindAccountProcess();
            }

            if (pid != 0)
            {
                lastSeen = DateTime.UtcNow;
                foreach (var hwnd in WindowsOf(pid))
                    if (tagged.Add(hwnd)) Tag(hwnd);
            }
            else if (DateTime.UtcNow - lastSeen > TimeSpan.FromSeconds(30))
            {
                return;   // the account has quit; nothing left to keep apart
            }

            Thread.Sleep(pid != 0 ? 1000 : 2000);
        }
    }

    static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    // The main Claude process for this account is the one started with its folder and no --type.
    static int FindAccountProcess()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'claude.exe'"))
                foreach (ManagementObject o in searcher.Get())
                    using (o)
                    {
                        var cmd = o["CommandLine"] as string ?? "";
                        bool mine = Number == 1
                            ? cmd.IndexOf("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase) >= 0 && cmd.IndexOf("--user-data-dir", StringComparison.OrdinalIgnoreCase) < 0
                            : cmd.IndexOf(Profile + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (mine && cmd.IndexOf("--type=", StringComparison.Ordinal) < 0)
                            return Convert.ToInt32(o["ProcessId"]);
                    }
        }
        catch { }
        return 0;
    }

    static List<IntPtr> WindowsOf(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h) && GetWindow(h, GW_OWNER) == IntPtr.Zero)
                found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // The relaunch properties go on before the id: the taskbar reads them when the id lands.
    static void Tag(IntPtr hwnd)
    {
        IPropertyStore store;
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0) return;
        try
        {
            Set(store, 2, "\"" + Self + "\"");
            Set(store, 3, (IconFile != null && File.Exists(IconFile) ? IconFile : Self) + ",0");
            Set(store, 4, DisplayName);
            Set(store, 5, AppId);
            store.Commit();
            SetWindowIcon(hwnd);
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    static void Set(IPropertyStore store, uint pid, string value)
    {
        var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = pid };
        var v = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, v); }
        finally { Marshal.FreeCoTaskMem(v.p); }
    }

    // Changing the app id makes the taskbar drop Claude's own icon, so give the window ours (taken from this exe).
    static IntPtr big, small;
    static void SetWindowIcon(IntPtr hwnd)
    {
        try
        {
            if (big == IntPtr.Zero)
            {
                var h = new IntPtr[1]; var id = new uint[1];
                if (PrivateExtractIcons(Self, 0, 256, 256, h, id, 1, 0) > 0) big = h[0];
                if (PrivateExtractIcons(Self, 0, 16, 16, h, id, 1, 0) > 0) small = h[0];
            }
            if (big != IntPtr.Zero) SendMessage(hwnd, 0x80, (IntPtr)1, big);
            if (small != IntPtr.Zero) SendMessage(hwnd, 0x80, IntPtr.Zero, small);
        }
        catch { }
    }

    const uint GW_OWNER = 4;
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint n, uint flags);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, [Out] PropVariant value);
        int SetValue(ref PropertyKey key, [In] PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    class PropVariant { public ushort vt; ushort r1, r2, r3; public IntPtr p; IntPtr p2; }
}
