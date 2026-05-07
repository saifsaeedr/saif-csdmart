using System.Text.RegularExpressions;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Port of dmart Python's utils/middleware.py::ChannelMiddleware. Gate keyed
// off the `x-channel-key` header that runs before route matching:
//
//   * settings.EnableChannelAuth = false  → no-op pass-through.
//   * No header on request                → if the path matches any
//                                            channel's allowed_api_patterns,
//                                            reject with 403/channel_auth.
//                                            Else pass through.
//   * Header present but unknown key      → 403/channel_auth.
//   * Header present with valid key       → path must match that channel's
//                                            allowed_api_patterns, else 403.
//
// Pattern semantics: `Regex.IsMatch` (anywhere-in-string) — equivalent to
// Python's `pattern.search(path)`. Patterns are pre-compiled in
// ChannelsRegistry to avoid per-request work.
public static class ChannelAuthMiddleware
{
    public const string ChannelKeyHeader = "x-channel-key";

    public static IApplicationBuilder UseChannelAuth(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
            if (!settings.EnableChannelAuth)
            {
                await next();
                return;
            }

            var registry = ctx.RequestServices.GetRequiredService<ChannelsRegistry>();
            var log = ctx.RequestServices.GetRequiredService<ILogger<ChannelsRegistry>>();
            var path = ctx.Request.Path.Value ?? "/";

            // Match Python's `headers.get('x-channel-key')`: take the FIRST
            // value when multiple are present. StringValues.ToString() joins
            // with commas, which would never match a configured key.
            string? channelKey = null;
            if (ctx.Request.Headers.TryGetValue(ChannelKeyHeader, out var headerValues)
                && headerValues.Count > 0)
            {
                channelKey = headerValues[0];
            }

            if (string.IsNullOrEmpty(channelKey))
            {
                foreach (var ch in registry.Channels)
                {
                    foreach (var pattern in ch.AllowedApiPatterns)
                    {
                        if (SafeIsMatch(pattern, path, log, ch.Name))
                        {
                            log.LogWarning(
                                "channel-auth denied: missing x-channel-key for restricted path {Path} (channel={Channel})",
                                path, ch.Name);
                            await WriteForbidden(ctx, "Requested method or path is forbidden");
                            return;
                        }
                    }
                }
                await next();
                return;
            }

            ChannelsRegistry.Channel? matched = null;
            foreach (var ch in registry.Channels)
            {
                if (ch.Keys.Contains(channelKey))
                {
                    matched = ch;
                    break;
                }
            }

            if (matched is null)
            {
                log.LogWarning("channel-auth denied: unknown x-channel-key for {Path}", path);
                await WriteForbidden(ctx, "Requested method or path is forbidden [2]");
                return;
            }

            foreach (var pattern in matched.AllowedApiPatterns)
            {
                if (SafeIsMatch(pattern, path, log, matched.Name))
                {
                    await next();
                    return;
                }
            }

            log.LogWarning(
                "channel-auth denied: channel {Channel} not authorized for {Path}",
                matched.Name, path);
            await WriteForbidden(ctx, "Requested method or path is forbidden [3]");
        });
    }

    // Wraps Regex.IsMatch so a pattern that exceeds its match-timeout (set in
    // ChannelsRegistry) doesn't escape as an unhandled RegexMatchTimeoutException
    // and 500 the request — log it and treat as a non-match. The combination of
    // a 100ms timeout + a non-match fallback makes the gate ReDoS-resistant.
    private static bool SafeIsMatch(Regex pattern, string path, ILogger log, string channelName)
    {
        try { return pattern.IsMatch(path); }
        catch (RegexMatchTimeoutException)
        {
            log.LogError("channel-auth: regex timeout on channel={Channel} pattern={Pattern} path={Path}",
                channelName, pattern, path);
            return false;
        }
    }

    private static async Task WriteForbidden(HttpContext ctx, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "application/json";
        var body = Response.Fail(InternalErrorCode.NOT_ALLOWED, message, ErrorTypes.ChannelAuth);
        await ctx.Response.WriteAsJsonAsync(body, DmartJsonContext.Default.Response);
    }
}
