using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Port of dmart/backend/main.py::set_middleware_response_headers.
//
// Responsibilities, in order:
//   1. CORS — if the configured AllowedCorsOrigins list is non-empty and the
//      request's Origin header matches one of the entries, reflect the origin
//      and set Access-Control-Allow-Credentials: true. If the list is non-empty
//      but the origin doesn't match, we emit NO CORS headers at all — the
//      browser will block the request. If the list is empty, fall back to
//      "same-host only" using {ListeningHost}:{ListeningPort} so we never open
//      up arbitrary reflection.
//   2. Static Allow-Headers, Allow-Methods, Max-Age, Expose-Headers so every
//      response carries the same CORS contract Python does.
//   3. Security headers (X-Content-Type-Options, X-Frame-Options, Referrer-Policy,
//      Permissions-Policy, Strict-Transport-Security) — added on every response
//      regardless of CORS outcome.
//   4. No-cache Cache-Control on API responses + x-server-time timestamp.
//   5. Short-circuit OPTIONS preflight with 204 so the browser can complete the
//      preflight without hitting the route layer (which would 405).
//
// Reading settings via IOptions<DmartSettings> on every request picks up
// IOptionsMonitor-style live reloads if a future provider supports it, and
// keeps the middleware allocation-free otherwise.
public static class ResponseHeadersMiddleware
{
    // Static header values that Python sets verbatim — precomputed so we don't
    // concat strings per request.
    private const string AllowHeaders = "content-type, charset, authorization, accept-language, content-length";
    private const string AllowMethods = "OPTIONS, DELETE, POST, GET, PATCH, PUT";
    private const string MaxAge = "600";
    private const string ExposeHeaders = "x-server-time";
    private const string CacheControlNoCache = "no-cache, no-store, must-revalidate";
    private const string PermissionsPolicy = "geolocation=(), camera=(), microphone=()";
    private const string Hsts = "max-age=31536000; includeSubDomains";

    public static IApplicationBuilder UseDmartResponseHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
            var origin = ctx.Request.Headers.Origin.ToString();
            var allowlist = settings.ParseAllowedCorsOrigins();

            // Register an OnStarting callback so the headers land right before
            // the response body is flushed. Writing them here would be lost if
            // a downstream handler cleared the headers dictionary before
            // responding (which Kestrel does for some error paths).
            ctx.Response.OnStarting(() =>
            {
                var headers = ctx.Response.Headers;

                // --- CORS allowlist / fallback ---
                if (allowlist.Length > 0)
                {
                    if (!string.IsNullOrEmpty(origin) && Array.IndexOf(allowlist, origin) >= 0)
                    {
                        headers["Access-Control-Allow-Origin"] = origin;
                        headers["Access-Control-Allow-Credentials"] = "true";
                    }
                    // else: intentionally emit NO CORS headers, matching Python.
                }
                else
                {
                    // Fallback: same-host only. Reflect the origin iff it matches
                    // http://{ListeningHost}:{ListeningPort} — otherwise always
                    // respond with that canonical same-host form so the browser
                    // can see a deterministic value without us reflecting
                    // arbitrary origins.
                    var defaultOrigin = $"http://{settings.ListeningHost}:{settings.ListeningPort}";
                    headers["Access-Control-Allow-Origin"] =
                        string.Equals(origin, defaultOrigin, StringComparison.Ordinal) ? origin : defaultOrigin;
                    headers["Access-Control-Allow-Credentials"] = "true";
                }

                // --- Static CORS contract ---
                headers["Access-Control-Allow-Headers"] = AllowHeaders;
                headers["Access-Control-Allow-Methods"] = AllowMethods;
                headers["Access-Control-Max-Age"] = MaxAge;
                headers["Access-Control-Expose-Headers"] = ExposeHeaders;

                // --- Cache-Control + timestamp ---
                // The C# port doesn't currently serve static assets, so the
                // path-based cache-control branching Python does for
                // .js/.css/.png is unnecessary. All our responses are API
                // responses that must not be cached.
                headers["Cache-Control"] = CacheControlNoCache;
                headers["Pragma"] = "no-cache";
                headers["Expires"] = "0";
                headers["x-server-time"] = DateTime.UtcNow.ToString("o");

                // --- Security headers ---
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = PermissionsPolicy;
                headers["Strict-Transport-Security"] = Hsts;

                return Task.CompletedTask;
            });

            // Preflight short-circuit. ASP.NET minimal APIs respond 405 to
            // OPTIONS by default since we don't register OPTIONS routes — so
            // we intercept here and return 204 with the CORS headers set via
            // the OnStarting callback above. Matches what FastAPI + CORSMiddleware
            // does in Python.
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next();
        });
    }
}
