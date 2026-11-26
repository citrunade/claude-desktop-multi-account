using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Sends the hand-off message for you: brings the new chat's window to the front, holds your mouse
// and keyboard still for a split second so nothing else gets the key, presses Enter, and lets go.
// It only presses Enter when that exact Claude window is in front; otherwise it leaves it to you.
static class AutoSend
{
    // Call from any thread after the new chat was opened. ui: a control whose thread pumps messages.
    public static void Run(int n, Control ui, Action<bool> done)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(3500);   // let the new chat load and fill in the message
            IntPtr win = Data.AccountWindow(n);
            if (win == IntPtr.Zero) { Report(ui, done, false); return; }
            try { ui.BeginInvoke((Action)(() => PressEnter(win, ok => done(ok)))); }
            catch { Report(ui, done, false); }
        });
    }

    // The hand-off for a chat with a folder. Opening a chat from outside the app in a folder always makes
    // Claude ask "Trust this workspace?", so instead this works the app like you would: brings the account's
    // window forward (starting the account if it isn't open), presses Ctrl+Shift+N (new chat with the
    // current settings), pastes the hand-off over anything left in the box and, with send, presses Enter.
    // done(false) when it couldn't get the window in front; nothing was typed then.
    public static void NewChatAndPaste(int n, Control ui, string text, bool send, Action<bool> done)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            IntPtr win = Data.AccountWindow(n);
            if (win == IntPtr.Zero)
            {
                Data.LaunchAccount(n);
                for (int i = 0; i < 50 && win == IntPtr.Zero; i++) { Thread.Sleep(500); win = Data.AccountWindow(n); }
                if (win == IntPtr.Zero) { Report(ui, done, false); return; }
                Thread.Sleep(5000);   // let a freshly started account finish loading
            }
            try
            {
                ui.BeginInvoke((Action)(() =>
                {
                    try { Clipboard.SetText(text); } catch { done(false); return; }
                    NewChatThenPaste(win, send, done);
                }));
            }
            catch { Report(ui, done, false); }
        });
    }

    // On the UI thread. Holds other input for the few seconds this takes.
    static void NewChatThenPaste(IntPtr win, bool send, Action<bool> done)
    {
        Lock();
        var safety = new System.Windows.Forms.Timer { Interval = 7000 };   // never hold input longer than this
        safety.Tick += (s, e) => { safety.Stop(); safety.Dispose(); Unlock(); };
        safety.Start();
        Data.Focus(win);
        int tries = 0;
        var t = new System.Windows.Forms.Timer { Interval = 150 };
        t.Tick += (s, e) =>
        {
            tries++;
            bool front = GetForegroundWindow() == win;
            if (!front && tries < 12) { Data.Focus(win); return; }
            t.Stop(); t.Dispose();
            if (!front) { Note("new chat not opened, window not in front after " + tries + " tries"); Unlock(); done(false); return; }
            SendKeys(0x1B);   // Escape first: Ctrl+Shift+N does nothing while focus sits in an open chat's page
            After(250, () => SendKeys(0x11, 0x10, 0x4E));   // Ctrl+Shift+N: new chat with the current settings
            After(1850, () =>
            {
                if (GetForegroundWindow() != win) { Note("window lost the front before pasting"); Unlock(); done(false); return; }
                // Clear any unsent draft left in the box (Ctrl+A doesn't select it there): Ctrl+End,
                // Ctrl+Shift+Home, Delete.
                SendKeys(0x11, 0x23);
                After(120, () => SendKeys(0x11, 0x10, 0x24));
                After(240, () => SendKeys(0x2E));
                After(450, () => SendKeys(0x11, 0x56));   // Ctrl+V: the hand-off
                After(1450, () =>
                {
                    bool still = GetForegroundWindow() == win;
                    if (send && still) SendEnter();
                    Note("new chat, pasted" + (send ? (still ? ", enter sent" : ", enter not sent (window lost the front)") : ", left for you to send"));
                    After(150, () => { Unlock(); done(true); });
                });
            });
        };
        t.Start();
    }

    static void After(int ms, Action a)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
        t.Start();
    }

    // Presses the keys in order and lets them go in reverse (a shortcut such as Ctrl+Shift+N).
    static void SendKeys(params ushort[] vks)
    {
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++) { inputs[i].type = 1; inputs[i].ki.wVk = vks[i]; inputs[i].ki.dwExtraInfo = Mine; }
        for (int i = 0; i < vks.Length; i++)
        {
            var k = vks.Length + i;
            inputs[k].type = 1; inputs[k].ki.wVk = vks[vks.Length - 1 - i]; inputs[k].ki.dwFlags = 2; inputs[k].ki.dwExtraInfo = Mine;
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    static void Report(Control ui, Action<bool> done, bool ok)
    {
        try { ui.BeginInvoke((Action)(() => done(ok))); } catch { }
    }

    // On the UI thread (the low-level hooks need its message loop).
    // Holds all other input (yours and other programs', such as another Claude session driving the
    // screen), brings the window forward, and presses Enter the moment it is in front, trying again a
    // few times if something else takes the front in between.
    public static void PressEnter(IntPtr win, Action<bool> done)
    {
        Lock();
        var safety = new System.Windows.Forms.Timer { Interval = 2000 };   // never hold input longer than this
        safety.Tick += (s, e) => { safety.Stop(); safety.Dispose(); Unlock(); };
        safety.Start();
        Data.Focus(win);
        int tries = 0;
        var t = new System.Windows.Forms.Timer { Interval = 150 };
        t.Tick += (s, e) =>
        {
            tries++;
            bool front = GetForegroundWindow() == win;
            if (!front && tries < 8) { Data.Focus(win); return; }
            t.Stop(); t.Dispose();
            if (front) SendEnter();
            Note("enter " + (front ? "sent" : "not sent, window not in front") + " after " + tries + " tries");
            var release = new System.Windows.Forms.Timer { Interval = 150 };
            release.Tick += (s2, e2) => { release.Stop(); release.Dispose(); Unlock(); done(front); };
            release.Start();
        };
        t.Start();
    }

    static void Note(string line)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(ClaudeTray.Dir, "handoff.log"), DateTime.Now.ToString("MM-dd HH:mm:ss.ff") + " " + line + Environment.NewLine); } catch { }
    }

    // ---------- holding input still: swallow every mouse and key event, real or sent by another program,
    // except our own Enter (marked with Mine) ----------
    static readonly IntPtr Mine = (IntPtr)0x434C5452;
    static IntPtr kbHook = IntPtr.Zero, msHook = IntPtr.Zero;
    static readonly HookProc kbProc = KeyboardProc, msProc = MouseProc;   // kept alive while hooked

    static void Lock()
    {
        if (kbHook != IntPtr.Zero) return;
        var mod = GetModuleHandle(null);
        kbHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, kbProc, mod, 0);
        msHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, msProc, mod, 0);
    }

    static void Unlock()
    {
        if (kbHook != IntPtr.Zero) { UnhookWindowsHookEx(kbHook); kbHook = IntPtr.Zero; }
        if (msHook != IntPtr.Zero) { UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; }
    }

    static IntPtr KeyboardProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            IntPtr extra = Marshal.ReadIntPtr(l, 16);
            // Let key releases through: swallowing one leaves that key stuck down for Windows
            // (a held-in Alt is what stops Alt+Tab working afterwards).
            int msg = (int)w;
            bool release = msg == 0x101 /* WM_KEYUP */ || msg == 0x105 /* WM_SYSKEYUP */;
            if (extra != Mine && !release) return (IntPtr)1;   // anyone else's key press: hold it
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    static IntPtr MouseProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            return (IntPtr)1;    // any mouse movement or click, real or sent: hold it
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    static void SendEnter()
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1; inputs[0].ki.wVk = 0x0D; inputs[0].ki.dwExtraInfo = Mine;
        inputs[1].type = 1; inputs[1].ki.wVk = 0x0D; inputs[1].ki.dwFlags = 2; inputs[1].ki.dwExtraInfo = Mine;   // key up
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; uint pad1; uint pad2; }
}
