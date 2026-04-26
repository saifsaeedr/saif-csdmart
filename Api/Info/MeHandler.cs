using Dmart.Models.Api;

namespace Dmart.Api.Info;

public static class MeHandler
{
    public static void Map(RouteGroupBuilder g) =>
        // AllowAnonymous lets the frontend use /info/me as a session probe:
        // unauthed callers get 200 with authenticated:false instead of a 401,
        // which keeps the browser console clean on cold loads.
        g.MapGet("/me", (HttpContext http) => Response.Ok(attributes: new()
        {
            ["shortname"] = http.ActorOrAnonymous(),
            ["authenticated"] = http.User.Identity?.IsAuthenticated ?? false,
        })).AllowAnonymous();
}
