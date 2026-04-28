using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Dmart.Plugins.Native;

// C-ABI callbacks that dmart hands to each native .so plugin at load time, so
// plugin hooks can reach back into dmart (read entries, save entries, send
// email, broadcast WS). See the matching `init(const DmartCallbacks*)` export
// described in custom_plugins_sdk/README.md.
//
// All string params are null-terminated UTF-8 (matches the existing hook()
// wire format). `char*` return values are malloc'd with AllocHGlobal and must
// be freed by the plugin via `dmart_free`; `int` return values use 0 = ok,
// non-zero = error.
//
// Thread model: callbacks are sync (the plugin's hook() is sync from the C
// side — see NativePluginHandle.CallHook). Async dmart services are bridged
// with GetAwaiter().GetResult(). This is safe because:
//   - each callback invocation runs on a thread-pool thread that dmart
//     already allocated for the hook dispatch;
//   - we never call back into managed code from inside the sync wait, so
//     no deadlock risk.
//
// AOT: every managed method exposed as a function pointer is marked with
// [UnmanagedCallersOnly(CallConvs=[CallConvCdecl])]. No reflection, no
// dynamic delegate creation — fully AOT-friendly.
public static unsafe class NativePluginCallbacks
{
    // Layout MUST match the C struct in custom_plugins_sdk/README.md and
    // shared/DmartCallbacks.cs. Adding fields is a breaking change for all
    // deployed .so plugins — append, never reorder.
    [StructLayout(LayoutKind.Sequential)]
    public struct DmartCallbacks
    {
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, byte*> LoadEntry;
        public delegate* unmanaged[Cdecl]<byte*, byte*> LoadUser;
        public delegate* unmanaged[Cdecl]<byte*, int> SaveEntry;
        public delegate* unmanaged[Cdecl]<byte*, int> UpdateUser;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int> SendEmail;
        public delegate* unmanaged[Cdecl]<byte*, byte*, int> WsBroadcast;
        public delegate* unmanaged[Cdecl]<byte*, void> DmartFree;
        // Generic Query callback. Plugin sends a serialized Query JSON;
        // host runs it through the same QueryService the HTTP API uses.
        // By default the query is executed as the user that triggered the
        // hook (so user permissions are honored). The plugin can override
        // by adding "as_actor" to the JSON: a string impersonates that
        // user, JSON null bypasses ACLs (system). APPENDED.
        public delegate* unmanaged[Cdecl]<byte*, byte*> Query;
        // Media bytes for an attachment, by (space, subpath, shortname).
        // Returns the raw bytes via outBufLen (caller frees with DmartFree).
        // null when the attachment has no media or doesn't exist. APPENDED.
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, int*, byte*> GetMediaAttachment;
    }

    // Allocated once, stays alive for the process lifetime. Plugins keep
    // the pointer we hand them in their `init()` and dereference it as
    // needed from later hook() calls.
    private static DmartCallbacks _instance;
    private static IntPtr _instancePtr;

    // One-way bridge from DI into the [UnmanagedCallersOnly] static methods
    // below. Program.cs sets this right after `app.Build()`. The methods
    // resolve scoped services with CreateScope() on each call.
    public static IServiceProvider? Services { get; set; }

    // Fills the struct with function pointers and returns a stable pointer
    // that can be handed to every plugin's init() export.
    public static IntPtr GetCallbacksPtr()
    {
        if (_instancePtr != IntPtr.Zero) return _instancePtr;

        _instance = new DmartCallbacks
        {
            LoadEntry = &LoadEntryCb,
            LoadUser = &LoadUserCb,
            SaveEntry = &SaveEntryCb,
            UpdateUser = &UpdateUserCb,
            SendEmail = &SendEmailCb,
            WsBroadcast = &WsBroadcastCb,
            DmartFree = &DmartFreeCb,
            Query = &QueryCb,
            GetMediaAttachment = &GetMediaAttachmentCb,
        };
        // Copy into unmanaged memory so the pointer doesn't move under the
        // GC. The struct is small and ABI-stable; the pointer lives until
        // process exit (no Free).
        _instancePtr = Marshal.AllocHGlobal(Unsafe.SizeOf<DmartCallbacks>());
        Marshal.StructureToPtr(_instance, _instancePtr, false);
        return _instancePtr;
    }

    // ========================================================================
    // Callback implementations
    // ========================================================================

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static byte* LoadEntryCb(byte* space, byte* subpath, byte* shortname, byte* resourceType)
    {
        try
        {
            var sp = PtrToString(space) ?? "";
            var sub = PtrToString(subpath) ?? "";
            var sn = PtrToString(shortname) ?? "";
            var rtStr = PtrToString(resourceType);

            if (Services is null) return AllocUtf8("""{"error":"services_not_initialized"}""");
            using var scope = Services.CreateScope();
            var entries = scope.ServiceProvider.GetRequiredService<EntryRepository>();

            Entry? entry;
            if (!string.IsNullOrEmpty(rtStr) && TryParseResourceType(rtStr, out var rt))
                entry = entries.GetAsync(sp, sub, sn, rt).GetAwaiter().GetResult();
            else
                entry = entries.GetAsync(sp, sub, sn).GetAwaiter().GetResult();

            if (entry is null) return AllocUtf8("""{"entry":null}""");
            var json = JsonSerializer.Serialize(entry, DmartJsonContext.Default.Entry);
            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            return AllocUtf8($$"""{"error":{{JsonEncode(ex.Message)}}}""");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static byte* LoadUserCb(byte* shortname)
    {
        try
        {
            var sn = PtrToString(shortname) ?? "";
            if (Services is null) return AllocUtf8("""{"error":"services_not_initialized"}""");
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserRepository>();
            var user = users.GetByShortnameAsync(sn).GetAwaiter().GetResult();
            if (user is null) return AllocUtf8("""{"user":null}""");
            var json = JsonSerializer.Serialize(user, DmartJsonContext.Default.User);
            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            return AllocUtf8($$"""{"error":{{JsonEncode(ex.Message)}}}""");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int SaveEntryCb(byte* entryJson)
    {
        try
        {
            var json = PtrToString(entryJson);
            if (string.IsNullOrEmpty(json) || Services is null) return 1;
            var entry = JsonSerializer.Deserialize(json, DmartJsonContext.Default.Entry);
            if (entry is null) return 2;
            using var scope = Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<EntryRepository>();
            repo.UpsertAsync(entry).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugin_cb] save_entry failed: {ex.Message}");
            return 3;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int UpdateUserCb(byte* userJson)
    {
        try
        {
            var json = PtrToString(userJson);
            if (string.IsNullOrEmpty(json) || Services is null) return 1;
            var user = JsonSerializer.Deserialize(json, DmartJsonContext.Default.User);
            if (user is null) return 2;
            using var scope = Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<UserRepository>();
            repo.UpsertAsync(user).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugin_cb] update_user failed: {ex.Message}");
            return 3;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int SendEmailCb(byte* to, byte* subject, byte* html)
    {
        try
        {
            var t = PtrToString(to);
            var s = PtrToString(subject);
            var b = PtrToString(html);
            if (string.IsNullOrEmpty(t) || Services is null) return 1;
            using var scope = Services.CreateScope();
            var smtp = scope.ServiceProvider.GetRequiredService<SmtpSender>();
            var ok = smtp.SendEmailAsync(t!, s ?? "", b ?? "").GetAwaiter().GetResult();
            return ok ? 0 : 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugin_cb] send_email failed: {ex.Message}");
            return 3;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int WsBroadcastCb(byte* channel, byte* message)
    {
        try
        {
            var ch = PtrToString(channel);
            var msg = PtrToString(message);
            if (string.IsNullOrEmpty(ch) || Services is null) return 1;
            // WsConnectionManager is a singleton so no scope needed, but use
            // CreateScope for consistency — the resolver short-circuits on
            // singletons.
            using var scope = Services.CreateScope();
            var ws = scope.ServiceProvider.GetRequiredService<WsConnectionManager>();
            var ok = ws.BroadcastToChannelAsync(ch!, msg ?? "").GetAwaiter().GetResult();
            return ok ? 0 : 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugin_cb] ws_broadcast failed: {ex.Message}");
            return 3;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void DmartFreeCb(byte* ptr)
    {
        if (ptr != null) Marshal.FreeHGlobal((IntPtr)ptr);
    }

    // Plugin → host query: same QueryService the HTTP API uses. By default
    // the query runs as the user that triggered the hook (so user
    // permissions are honored). A plugin can override the actor by adding
    // an "as_actor" field to the query JSON:
    //   - field absent       → caller's actor (PluginInvocationContext)
    //   - field = "username"  → impersonate that user
    //   - field = null       → no actor (system / no ACL)
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static byte* QueryCb(byte* queryJson)
    {
        try
        {
            var json = PtrToString(queryJson);
            if (string.IsNullOrEmpty(json) || Services is null)
                return AllocUtf8(BuildQueryFailJson(InternalErrorCode.SOMETHING_WRONG,
                    "empty query or services not initialized", ErrorTypes.Internal));

            // Resolve the effective actor: explicit override from the JSON if
            // the plugin set one, otherwise the caller carried via the
            // ambient PluginInvocationContext. The Query model ignores this
            // field on deserialize.
            string? actor;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("as_actor", out var asActor))
                {
                    actor = asActor.ValueKind == JsonValueKind.Null
                        ? null
                        : asActor.GetString();
                }
                else
                {
                    actor = PluginInvocationContext.CurrentActor;
                }
            }
            catch (JsonException jex)
            {
                return AllocUtf8(BuildQueryFailJson(InternalErrorCode.INVALID_DATA,
                    $"invalid query json: {jex.Message}", ErrorTypes.Request));
            }

            Query? query;
            try { query = JsonSerializer.Deserialize(json, DmartJsonContext.Default.Query); }
            catch (JsonException jex)
            {
                return AllocUtf8(BuildQueryFailJson(InternalErrorCode.INVALID_DATA,
                    $"invalid query json: {jex.Message}", ErrorTypes.Request));
            }
            if (query is null)
                return AllocUtf8(BuildQueryFailJson(InternalErrorCode.INVALID_DATA,
                    "invalid query json", ErrorTypes.Request));
            using var scope = Services.CreateScope();
            var qsvc = scope.ServiceProvider.GetRequiredService<QueryService>();
            var resp = qsvc.ExecuteAsync(query, actor).GetAwaiter().GetResult();
            return AllocUtf8(JsonSerializer.Serialize(resp, DmartJsonContext.Default.Response));
        }
        catch (Exception ex)
        {
            return AllocUtf8(BuildQueryFailJson(InternalErrorCode.SOMETHING_WRONG, ex.Message, ErrorTypes.Exception));
        }
    }

    // internal for testing via dmart.Tests.
    internal static string BuildQueryFailJson(int code, string message, string type)
        => JsonSerializer.Serialize(Response.Fail(code, message, type), DmartJsonContext.Default.Response);

    // Plugin → host: fetch the raw `media` BYTEA for an attachment by
    // (space, subpath, shortname). Returns the bytes via outBufLen and a
    // pointer that the plugin must release with DmartFree. Returns null
    // when the attachment is missing or has no media.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static byte* GetMediaAttachmentCb(byte* space, byte* subpath, byte* shortname, int* outBufLen)
    {
        try
        {
            if (outBufLen != null) *outBufLen = 0;
            var sp = PtrToString(space) ?? "";
            var sub = PtrToString(subpath) ?? "";
            var sn = PtrToString(shortname) ?? "";
            if (Services is null) return null;
            using var scope = Services.CreateScope();
            var atts = scope.ServiceProvider.GetRequiredService<AttachmentRepository>();
            var att = atts.GetAsync(sp, sub, sn).GetAwaiter().GetResult();
            if (att is null || !Guid.TryParse(att.Uuid, out var uuid)) return null;
            var (bytes, _) = atts.GetMediaAsync(uuid).GetAwaiter().GetResult();
            if (bytes is null || bytes.Length == 0) return null;
            var ptr = (byte*)Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, (IntPtr)ptr, bytes.Length);
            if (outBufLen != null) *outBufLen = bytes.Length;
            return ptr;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[plugin_cb] get_media_attachment failed: {ex.Message}");
            if (outBufLen != null) *outBufLen = 0;
            return null;
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static string? PtrToString(byte* ptr)
        => ptr == null ? null : Marshal.PtrToStringUTF8((IntPtr)ptr);

    private static byte* AllocUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return (byte*)ptr;
    }

    // Minimal JSON string encoder for the error-path only (avoids bringing
    // JsonSerializer into every error return).
    private static string JsonEncode(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static bool TryParseResourceType(string raw, out ResourceType value)
    {
        foreach (var a in Enum.GetValues<ResourceType>())
        {
            if (JsonbHelpers.EnumMember(a) == raw) { value = a; return true; }
        }
        value = default;
        return false;
    }
}
