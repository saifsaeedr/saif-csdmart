using System.Reflection;
using Dmart.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Serves the CXB Svelte SPA at /cxb from either:
//   1. Embedded resources (native binary on host — ManifestEmbeddedFileProvider)
//   2. Filesystem at {BaseDir}/cxb/ (Docker — native AOT on musl doesn't
//      support ManifestEmbeddedFileProvider reliably)
//
// SPA fallback: /cxb/* paths without file extensions → index.html.
// Dynamic config.json with Python-parity fallback chain.
public static class CxbMiddleware
{
    public static IApplicationBuilder UseCxb(this IApplicationBuilder app)
    {
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

        // Strategy 2: Filesystem at {BaseDir}/cxb/ (Docker).
        if (fileProvider is null)
        {
            var fsPath = Path.Combine(AppContext.BaseDirectory, "cxb");
            if (File.Exists(Path.Combine(fsPath, "index.html")))
                fileProvider = new PhysicalFileProvider(fsPath);
        }

        // No CXB available — skip silently (dev builds without build-cxb.sh).
        if (fileProvider is null) return app;

        // Dynamic config.json — MUST be before UseStaticFiles so it intercepts
        // /cxb/config.json before the embedded/filesystem static file is served.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/cxb/config.json"))
            {
                var settings = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
                var paths = new[]
                {
                    Environment.GetEnvironmentVariable("DMART_CXB_CONFIG"),
                    "config.json",
                    Path.Combine(settings.SpacesRoot, "config.json"),
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

        // Serve static files at /cxb.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = "/cxb",
        });

        // SPA fallback — /cxb/* without file extension → index.html.
        app.Use(async (ctx, next) =>
        {
            await next();
            if (ctx.Response.StatusCode == 404
                && !ctx.Response.HasStarted
                && ctx.Request.Path.StartsWithSegments("/cxb")
                && !Path.HasExtension(ctx.Request.Path.Value))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html";
                var file = fileProvider.GetFileInfo("index.html");
                if (file.Exists)
                {
                    await using var stream = file.CreateReadStream();
                    await stream.CopyToAsync(ctx.Response.Body);
                }
            }
        });

        return app;
    }
}
