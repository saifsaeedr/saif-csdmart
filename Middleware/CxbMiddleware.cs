using System.Reflection;
using System.Text;
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
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers["Cache-Control"] = "no-cache";
                        await ctx.Response.SendFileAsync(p);
                        return;
                    }
                }
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
}
