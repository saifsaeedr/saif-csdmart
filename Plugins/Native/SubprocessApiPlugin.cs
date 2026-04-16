using System.Text.Json;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// IApiPlugin adapter that delegates to a subprocess via stdin/stdout JSON lines.
internal sealed class SubprocessApiPlugin : IApiPlugin
{
    private readonly SubprocessPluginHost _host;
    private readonly List<NativeApiPlugin.NativeRoute> _routes;

    public string Shortname => _host.Shortname;

    public SubprocessApiPlugin(SubprocessPluginHost host, List<NativeApiPlugin.NativeRoute> routes)
    {
        _host = host;
        _routes = routes;
    }

    public void MapRoutes(RouteGroupBuilder group)
    {
        foreach (var route in _routes)
        {
            var h = _host;
            switch (route.Method.ToUpperInvariant())
            {
                case "GET":    group.MapGet(route.Path, async (HttpContext ctx) => await Handle(ctx, h)); break;
                case "POST":   group.MapPost(route.Path, async (HttpContext ctx) => await Handle(ctx, h)); break;
                case "PUT":    group.MapPut(route.Path, async (HttpContext ctx) => await Handle(ctx, h)); break;
                case "DELETE": group.MapDelete(route.Path, async (HttpContext ctx) => await Handle(ctx, h)); break;
                default:       group.MapGet(route.Path, async (HttpContext ctx) => await Handle(ctx, h)); break;
            }
        }

        if (_routes.Count == 0)
        {
            var h = _host;
            group.MapGet("/", async (HttpContext ctx) => await Handle(ctx, h));
        }
    }

    private static async Task Handle(HttpContext ctx, SubprocessPluginHost host)
    {
        var query = new Dictionary<string, string>();
        foreach (var q in ctx.Request.Query)
            query[q.Key] = q.Value.ToString();

        var headers = new Dictionary<string, string>();
        foreach (var h in ctx.Request.Headers)
            headers[h.Key] = h.Value.ToString();

        string? body = null;
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContentType.Count > 0)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            body = await reader.ReadToEndAsync();
        }

        var envelope = new NativeApiRequest
        {
            Method = ctx.Request.Method,
            Path = ctx.Request.Path.Value ?? "/",
            Query = query,
            Headers = headers,
            Body = body,
            User = ctx.User.Identity?.Name,
        };

        var requestJson = JsonSerializer.Serialize(envelope, DmartJsonContext.Default.NativeApiRequest);
        var wrapped = $"{{\"type\":\"request\",\"request\":{requestJson}}}";
        var resultJson = host.SendAndReceive(wrapped);

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(resultJson);
    }
}
