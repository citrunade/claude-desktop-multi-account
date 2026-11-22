using System;
using System.Runtime.InteropServices;

// Puts a taskbar group id (AppUserModelID) on a .lnk file, so a pinned or desktop shortcut and the
// windows it opens share one taskbar button, and a new id makes the taskbar fetch the icon afresh.
static class ShellLinkId
{
    public static void Set(string lnk, string appId)
    {
        if (string.IsNullOrEmpty(appId)) return;
        object link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            var file = (IPersistFile)link;
            file.Load(lnk, 2 /* STGM_READWRITE */);
            var store = (IPropertyStore)link;
            var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            var v = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(appId) };
            try { store.SetValue(ref key, v); store.Commit(); }
            finally { Marshal.FreeCoTaskMem(v.p); }
            file.Save(lnk, true);
        }
        catch { }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

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
