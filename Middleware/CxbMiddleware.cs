using System.Reflection;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Serves the CXB Svelte SPA from either:
//   1. Embedded resources (native binary on host — ManifestEmbeddedFileProvider)
//   2. Filesystem at {BaseDir}/cxb/ (Docker — native AOT on musl doesn't
//      support ManifestEmbeddedFileProvider reliably)
//
// The URL prefix is configurable via CXB_URL in config.env (default: /cxb).
// The <base href> in index.html is rewritten at startup to match CXB_URL.
// SPA fallback: {cxbUrl}/* paths without file extensions → index.html.
// Dynamic config.json with Python-parity fallback chain.
public static class CxbMiddleware
{
    public static IApplicationBuilder UseCxb(this IApplicationBuilder app)
    {
        // Read CXB_URL from settings — normalize to start with / and end with /
        var settings = app.ApplicationServices.GetRequiredService<IOptions<DmartSettings>>().Value;
        var cxbUrl = settings.CxbUrl?.Trim().TrimEnd('/') ?? "/cxb";
        if (!cxbUrl.StartsWith('/')) cxbUrl = "/" + cxbUrl;
        var baseHref = cxbUrl + "/";  // <base href> needs trailing slash

        IFileProvider? fileProvider = null;

        // Strategy 1: Embedded resources (works on glibc/host builds).
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var embedded = new ManifestEmbeddedFileProvider(assembly, "cxb/dist/client");
            if (embedded.GetFileInfo("index.html").Exists)
                fileProvider = embedded;
        }
        catch { /* native AOT on musl — fall through */ }

        // Strategy 2: Filesystem fallback (Docker / RPM).
        if (fileProvider is null)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "cxb"),
                Path.Combine(Directory.GetCurrentDirectory(), "cxb"),
                "/usr/lib/dmart/cxb",
                "/app/cxb",
            };
            foreach (var fsPath in candidates)
            {
                if (File.Exists(Path.Combine(fsPath, "index.html")))
                {
                    fileProvider = new PhysicalFileProvider(fsPath);
                    break;
                }
            }
        }

        // No CXB available — skip silently (dev builds without build-cxb.sh).
        if (fileProvider is null) return app;

        // Pre-read index.html and rewrite <base href="/cxb/"> to match CXB_URL.
        // Done once at startup so there's no per-request cost.
        byte[]? indexHtmlBytes = null;
        var indexFile = fileProvider.GetFileInfo("index.html");
        if (indexFile.Exists)
        {
            using var stream = indexFile.CreateReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var html = reader.ReadToEnd();
            html = html.Replace("<base href=\"/cxb/\"", $"<base href=\"{baseHref}\"");
            html = html.Replace("<base href='/cxb/'", $"<base href='{baseHref}'");
            indexHtmlBytes = Encoding.UTF8.GetBytes(html);
        }

        // Dynamic config.json — MUST be before UseStaticFiles so it intercepts
        // {cxbUrl}/config.json before the embedded/filesystem static file is served.
        // Rewrites `backend` and `websocket` fields based on the incoming
        // request's scheme+host so the SPA always targets whatever URL the
        // browser used to reach dmart (even through reverse proxies).
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments($"{cxbUrl}/config.json"))
            {
                var s = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
                var paths = new[]
                {
                    Environment.GetEnvironmentVariable("DMART_CXB_CONFIG"),
                    "config.json",
                    Path.Combine(s.SpacesRoot, "config.json"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dmart", "config.json"),
                };
                foreach (var p in paths)
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        var bytes = await File.ReadAllBytesAsync(p);
                        var rewritten = RewriteCxbConfig(bytes, ctx);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers["Cache-Control"] = "no-cache";
                        ctx.Response.ContentLength = rewritten.Length;
                        await ctx.Response.Body.WriteAsync(rewritten);
                        return;
                    }
                }
            }
            await next();
        });

        // Browser auto-requests /favicon.ico at the root — redirect to CXB's.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Redirect($"{cxbUrl}/favicon.ico");
                return;
            }
            await next();
        });

        // Intercept direct requests for index.html to serve the rewritten version.
        app.Use(async (ctx, next) =>
        {
            if (indexHtmlBytes is not null &&
                (ctx.Request.Path.Equals($"{cxbUrl}/index.html", StringComparison.OrdinalIgnoreCase) ||
                 ctx.Request.Path.Equals($"{cxbUrl}/", StringComparison.OrdinalIgnoreCase) ||
                 ctx.Request.Path.Equals(cxbUrl, StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength = indexHtmlBytes.Length;
                await ctx.Response.Body.WriteAsync(indexHtmlBytes);
                return;
            }
            await next();
        });

        // Serve static files at {cxbUrl} (everything except index.html which is handled above).
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = cxbUrl,
        });

        // SPA fallback — {cxbUrl}/* without file extension → rewritten index.html.
        app.Use(async (ctx, next) =>
        {
            await next();
            if (ctx.Response.StatusCode == 404
                && !ctx.Response.HasStarted
                && ctx.Request.Path.StartsWithSegments(cxbUrl)
                && !Path.HasExtension(ctx.Request.Path.Value)
                && indexHtmlBytes is not null)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.Body.WriteAsync(indexHtmlBytes);
            }
        });

        return app;
    }

    // Parse config.json, overwrite `backend` with the request's own origin,
    // and return the rewritten bytes. Any other fields pass through untouched.
    // The SPA derives its WebSocket URL (ws(s)://{host}/ws) from `backend` at
    // the call site — config carries only the single source of truth.
    private static byte[] RewriteCxbConfig(byte[] source, HttpContext ctx)
    {
        var backend = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";

        try
        {
            using var doc = JsonDocument.Parse(source);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return source;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                var sawBackend = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("backend"))
                    {
                        writer.WriteString("backend", backend);
                        sawBackend = true;
                    }
                    else if (prop.NameEquals("websocket"))
                    {
                        // Legacy field — dropped so stale config.json files on
                        // disk don't leak obsolete URLs into the SPA.
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                if (!sawBackend) writer.WriteString("backend", backend);
                writer.WriteEndObject();
            }
            return ms.ToArray();
        }
        catch (JsonException)
        {
            return source;
        }
    }
}
