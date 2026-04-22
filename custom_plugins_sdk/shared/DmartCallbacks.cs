using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Dmart.Sdk;

// C# view of the DmartCallbacks struct dmart hands each native plugin via
// `init(const DmartCallbacks*)`. Layout MUST match the host definition in
// Plugins/Native/NativePluginCallbacks.cs — append fields only, never
// reorder.
//
// Usage in a plugin:
//
//     private static DmartCallbacks _cb;
//     private static bool _cbReady;
//
//     [UnmanagedCallersOnly(EntryPoint = "init")]
//     public static void Init(IntPtr cbsPtr)
//     {
//         _cb = Marshal.PtrToStructure<DmartCallbacks>(cbsPtr);
//         _cbReady = true;
//     }
//
//     [UnmanagedCallersOnly(EntryPoint = "hook")]
//     public static IntPtr Hook(IntPtr eventJsonPtr)
//     {
//         if (_cbReady) {
//             var user = DmartSdk.LoadUser(_cb, "dmart"); // returns JSON string
//             DmartSdk.SendEmail(_cb, "foo@example.com", "subject", "<p>body</p>");
//         }
//         ...
//     }
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DmartCallbacks
{
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, byte*> LoadEntry;
    public delegate* unmanaged[Cdecl]<byte*, byte*> LoadUser;
    public delegate* unmanaged[Cdecl]<byte*, int> SaveEntry;
    public delegate* unmanaged[Cdecl]<byte*, int> UpdateUser;
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int> SendEmail;
    public delegate* unmanaged[Cdecl]<byte*, byte*, int> WsBroadcast;
    public delegate* unmanaged[Cdecl]<byte*, void> DmartFree;
}

// Ergonomic wrappers that handle UTF-8 marshaling, null pointers, and
// freeing the returned string. Every method is self-contained so a plugin
// author can drop this single file into their project.
public static unsafe class DmartSdk
{
    public static string? LoadEntry(in DmartCallbacks cb, string space, string subpath,
        string shortname, string? resourceType = null)
    {
        if (cb.LoadEntry == null) return null;
        var spaceBuf = StringToUtf8(space);
        var subpathBuf = StringToUtf8(subpath);
        var shortnameBuf = StringToUtf8(shortname);
        var rtBuf = resourceType is null ? null : StringToUtf8(resourceType);
        try
        {
            var resultPtr = cb.LoadEntry(spaceBuf, subpathBuf, shortnameBuf, rtBuf);
            return TakeAndFree(cb, resultPtr);
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)spaceBuf);
            Marshal.FreeHGlobal((IntPtr)subpathBuf);
            Marshal.FreeHGlobal((IntPtr)shortnameBuf);
            if (rtBuf != null) Marshal.FreeHGlobal((IntPtr)rtBuf);
        }
    }

    public static string? LoadUser(in DmartCallbacks cb, string shortname)
    {
        if (cb.LoadUser == null) return null;
        var buf = StringToUtf8(shortname);
        try
        {
            var resultPtr = cb.LoadUser(buf);
            return TakeAndFree(cb, resultPtr);
        }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    public static int SaveEntry(in DmartCallbacks cb, string entryJson)
    {
        if (cb.SaveEntry == null) return -1;
        var buf = StringToUtf8(entryJson);
        try { return cb.SaveEntry(buf); }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    public static int UpdateUser(in DmartCallbacks cb, string userJson)
    {
        if (cb.UpdateUser == null) return -1;
        var buf = StringToUtf8(userJson);
        try { return cb.UpdateUser(buf); }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    public static int SendEmail(in DmartCallbacks cb, string to, string subject, string htmlBody)
    {
        if (cb.SendEmail == null) return -1;
        var toBuf = StringToUtf8(to);
        var subBuf = StringToUtf8(subject);
        var bodyBuf = StringToUtf8(htmlBody);
        try { return cb.SendEmail(toBuf, subBuf, bodyBuf); }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)toBuf);
            Marshal.FreeHGlobal((IntPtr)subBuf);
            Marshal.FreeHGlobal((IntPtr)bodyBuf);
        }
    }

    public static int WsBroadcast(in DmartCallbacks cb, string channel, string message)
    {
        if (cb.WsBroadcast == null) return -1;
        var chBuf = StringToUtf8(channel);
        var msgBuf = StringToUtf8(message);
        try { return cb.WsBroadcast(chBuf, msgBuf); }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)chBuf);
            Marshal.FreeHGlobal((IntPtr)msgBuf);
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static byte* StringToUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return (byte*)ptr;
    }

    private static string? TakeAndFree(in DmartCallbacks cb, byte* ptr)
    {
        if (ptr == null) return null;
        try { return Marshal.PtrToStringUTF8((IntPtr)ptr); }
        finally { if (cb.DmartFree != null) cb.DmartFree(ptr); }
    }
}
