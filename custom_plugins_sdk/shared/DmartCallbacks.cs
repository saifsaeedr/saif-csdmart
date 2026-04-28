using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Dmart.Sdk;
// V3
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
    // Generic Query — same shape as POST /managed/query body. By default
    // runs as the user that triggered the hook (user permissions honored);
    // include "as_actor" in the JSON (string to impersonate, null for
    // system / no ACL) to override.
    public delegate* unmanaged[Cdecl]<byte*, byte*> Query;
    // Media-attachment bytes, by (space, subpath, shortname). The 4th arg
    // is an int* the host writes the byte count to. Returned pointer must
    // be released via DmartFree. null when missing or no media column.
    public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int*, byte*> GetMediaAttachment;
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

    // Run a /managed/query-shaped JSON body through the host and get the
    // Response JSON back. By default the query runs as the user whose
    // action triggered the hook — i.e. user permissions are honored. To
    // override the actor (impersonate another user, or bypass ACLs as the
    // system), use QueryAs.
    public static string? Query(in DmartCallbacks cb, string queryJson)
    {
        if (cb.Query == null) return null;
        var buf = StringToUtf8(queryJson);
        try { return TakeAndFree(cb, cb.Query(buf)); }
        finally { Marshal.FreeHGlobal((IntPtr)buf); }
    }

    // Same as Query, but explicitly run as the given actor:
    //   - asActor = "username" → impersonate that user
    //   - asActor = null       → run with no actor (system / no ACL)
    // Injects/overrides the "as_actor" field in the JSON body, then
    // delegates to Query.
    public static string? QueryAs(in DmartCallbacks cb, string queryJson, string? asActor)
    {
        if (cb.Query == null) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(queryJson);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("as_actor")) continue;
                prop.WriteTo(writer);
            }
            if (asActor is null) writer.WriteNull("as_actor");
            else writer.WriteString("as_actor", asActor);
            writer.WriteEndObject();
        }
        return Query(in cb, Encoding.UTF8.GetString(ms.ToArray()));
    }

    // Read the raw `media` BYTEA for an attachment by (space, subpath,
    // shortname). null when the attachment is missing or has no media.
    // The returned byte[] is a managed copy — the unmanaged buffer the
    // host returned is released via DmartFree before this method returns.
    public static byte[]? GetMediaAttachment(in DmartCallbacks cb, string space, string subpath, string shortname)
    {
        if (cb.GetMediaAttachment == null) return null;
        var spBuf = StringToUtf8(space);
        var subBuf = StringToUtf8(subpath);
        var snBuf = StringToUtf8(shortname);
        try
        {
            int len = 0;
            var ptr = cb.GetMediaAttachment(spBuf, subBuf, snBuf, &len);
            if (ptr == null || len <= 0) return null;
            try
            {
                var bytes = new byte[len];
                Marshal.Copy((IntPtr)ptr, bytes, 0, len);
                return bytes;
            }
            finally { if (cb.DmartFree != null) cb.DmartFree(ptr); }
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)spBuf);
            Marshal.FreeHGlobal((IntPtr)subBuf);
            Marshal.FreeHGlobal((IntPtr)snBuf);
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
