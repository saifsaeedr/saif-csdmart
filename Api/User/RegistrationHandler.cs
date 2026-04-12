using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.User;

public static class RegistrationHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/create", async (HttpRequest req, UserService svc, CancellationToken ct) =>
        {
            var body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            if (body is null) return Response.Fail("bad_request", "missing body");
            var shortname = body.TryGetValue("shortname", out var sn) ? sn?.ToString() ?? "" : "";
            var email = body.TryGetValue("email", out var e) ? e?.ToString() : null;
            var msisdn = body.TryGetValue("msisdn", out var m) ? m?.ToString() : null;
            var password = body.TryGetValue("password", out var p) ? p?.ToString() : null;
            var language = body.TryGetValue("language", out var l) ? l?.ToString() : null;
            var result = await svc.CreateAsync(shortname, email, msisdn, password, language, ct);
            return result.IsOk
                ? Response.Ok(attributes: new() { ["uuid"] = result.Value!.Uuid, ["shortname"] = result.Value.Shortname })
                : Response.Fail(result.ErrorCode!, result.ErrorMessage!);
        });
    }
}
