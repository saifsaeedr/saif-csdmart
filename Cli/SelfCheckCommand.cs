using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Dmart.Cli;

// `dmart selfcheck` — operator-facing smoke that exercises the live HTTP
// surface of an already-running dmart instance: reachability, auth, a single
// space lookup, content-entry CRUD round-trip, and logout. Returns exit code
// 0 on full pass, 1 on any failure.
//
// This is NOT a parity suite — that's what dmart.Tests/curl.sh do, hitting
// ~90 endpoint shapes against a known fixture. selfcheck is for operators
// who just installed the package and want to confirm their dmart is up and
// answering as expected. The smoke runs in a few seconds, doesn't depend on
// any external tool (no jq/curl/file), and writes a styled Spectre report.
//
// JSON bodies are literal strings the same way Cli/DmartClient.cs builds them
// — keeps the path source-gen / AOT-safe with no JsonSerializer reflection.
public static class SelfCheckCommand
{
    public sealed record SelfCheckOptions(
        string Url,
        string Admin,
        string? Password,
        string? Space,
        string Subpath,
        bool Keep,
        bool Verbose);
    // Note: an earlier draft of this subcommand minted its own HS256 JWT from
    // the shared JWT_SECRET in config.env so the operator wouldn't need a
    // password at all. That doesn't work against this server's auth model —
    // Auth/JwtBearerSetup.cs:OnTokenValidated enforces a DB-backed session
    // row on every authenticated request ("Logout/password changes/account
    // deactivation delete session rows, so every non-bot request must still
    // have a live row"). Only /user/login creates those rows, so we go
    // through login. Reviving the mint approach would require either
    // provisioning a UserType.Bot account (bot users skip the session check)
    // or having selfcheck open a direct DB connection to insert a row —
    // both heavier than the current shape warrants.

    public static async Task<int> Run(string[] args, IReadOnlyDictionary<string, string?> dotenv)
    {
        var opts = ParseArgs(args, dotenv);
        if (opts is null) return 0; // --help printed; not an error

        // Per-call HttpClient — selfcheck is short-lived, no pooling needed.
        // Same 30s timeout as DmartClient; matches the operator expectation
        // that a hung server fails the smoke within seconds, not 100s later.
        using var http = new HttpClient
        {
            BaseAddress = new Uri(opts.Url.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await RunAsync(http, opts, AnsiConsole.Console);
    }

    // Testable body. `http.BaseAddress` must already be set by the caller — the
    // integration test feeds a TestServer-backed HttpClient straight in.
    public static async Task<int> RunAsync(HttpClient http, SelfCheckOptions opts, IAnsiConsole console)
    {
        console.MarkupLine($"[bold]dmart selfcheck[/] [grey]→ {Markup.Escape(opts.Url)}[/]");
        console.WriteLine();

        var passed = 0;
        var failed = 0;
        var failures = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step state shared across calls — token from login, the space
        // selfcheck creates entries in, and the shortname/subpath of the
        // entry under test so cleanup knows what to remove on failure too.
        string? token = null;
        string? targetSpace = null;
        var entryShortname = $"selfcheck_{Guid.NewGuid():N}"[..20];
        var entrySubpath = opts.Subpath;

        async Task<bool> RunStep(string name, Func<Task<(bool ok, string? detail)>> body)
        {
            try
            {
                var (ok, detail) = await body();
                if (ok)
                {
                    console.MarkupLine($"  [green]✓[/] {Markup.Escape(name)}" +
                        (opts.Verbose && detail is not null ? $" [grey]— {Markup.Escape(detail)}[/]" : ""));
                    passed++;
                    return true;
                }
                console.MarkupLine($"  [red]✗[/] {Markup.Escape(name)}" +
                    (detail is not null ? $" [red]— {Markup.Escape(detail)}[/]" : ""));
                failed++;
                failures.Add($"{name}: {detail ?? "failed"}");
                return false;
            }
            catch (Exception ex)
            {
                console.MarkupLine($"  [red]✗[/] {Markup.Escape(name)} [red]— {Markup.Escape(ex.GetType().Name + ": " + ex.Message)}[/]");
                failed++;
                failures.Add($"{name}: {ex.Message}");
                return false;
            }
        }

        // 1. Login — POST /user/login. Creates the session row that every
        //    subsequent authenticated request needs. If ADMIN_PASSWORD is
        //    unset in config.env and no --password was supplied, fail with a
        //    clear pointer at `dmart passwd` rather than letting an empty
        //    password slip through to the server.
        if (string.IsNullOrEmpty(opts.Password))
        {
            console.MarkupLine(
                "  [red]✗[/] no admin password available — set ADMIN_PASSWORD in config.env,");
            console.MarkupLine(
                "      run `dmart passwd " + Markup.Escape(opts.Admin) + " <pwd>`, or pass --password.");
            failures.Add("no password supplied");
            failed++;
            return Summarize(console, sw, passed, failed, failures);
        }

        if (!await RunStep($"login as {opts.Admin}", async () =>
        {
            var body = JsonBody($"{{\"shortname\":\"{Esc(opts.Admin)}\",\"password\":\"{Esc(opts.Password!)}\"}}");
            using var resp = await http.PostAsync("/user/login", body);
            var json = await ParseAsync(resp);
            if (!resp.IsSuccessStatusCode || !IsSuccess(json))
                return (false, ExtractError(json, $"HTTP {(int)resp.StatusCode}"));
            token = json.GetProperty("records")[0].GetProperty("attributes").GetProperty("access_token").GetString();
            if (string.IsNullOrEmpty(token))
                return (false, "login succeeded but no access_token in response");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (true, $"token ({token!.Length} bytes)");
        }))
        {
            return Summarize(console, sw, passed, failed, failures);
        }

        // 2. /info/manifest — the first server-touching request when the
        //    JWT-mint path was used. Doubles as a "server up + token valid"
        //    combined probe: a connection-refused here means dmart is down,
        //    a 401 means the JWT_SECRET / JwtIssuer / JwtAudience don't
        //    match the running server.
        if (!await RunStep("server reachable + token valid (/info/manifest)", async () =>
        {
            using var resp = await http.GetAsync("/info/manifest");
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}");
            var json = await ParseAsync(resp);
            if (!IsSuccess(json))
                return (false, "non-success manifest response");
            return (true, "manifest served");
        }))
        {
            return Summarize(console, sw, passed, failed, failures);
        }

        // 3. List spaces — pick the user-supplied --space if it exists,
        //    else the first non-system space. management always exists,
        //    but using it for CRUD pollutes the admin space; prefer a real
        //    content space (dummy if seeded).
        if (!await RunStep("list spaces", async () =>
        {
            var body = JsonBody("{\"type\":\"spaces\",\"space_name\":\"management\",\"subpath\":\"/\",\"search\":\"\",\"limit\":100}");
            using var resp = await http.PostAsync("/managed/query", body);
            var json = await ParseAsync(resp);
            if (!resp.IsSuccessStatusCode || !IsSuccess(json))
                return (false, ExtractError(json, $"HTTP {(int)resp.StatusCode}"));

            var spaces = new List<string>();
            if (json.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in recs.EnumerateArray())
                {
                    if (r.TryGetProperty("shortname", out var sn) && sn.GetString() is { Length: > 0 } name)
                        spaces.Add(name);
                }
            }
            if (spaces.Count == 0) return (false, "no spaces visible to this user");

            if (opts.Space is { Length: > 0 } pick && spaces.Contains(pick))
                targetSpace = pick;
            else
                targetSpace = spaces.FirstOrDefault(s => s != "management") ?? spaces[0];
            return (true, $"{spaces.Count} space(s); using '{targetSpace}'");
        }))
        {
            return Summarize(console, sw, passed, failed, failures);
        }

        // 4. Create — content entry at /{Subpath}/{entryShortname}. Body is
        //    a minimal valid Content payload; the upstream handler enforces
        //    shortname uniqueness + ACL, so this exercises both write paths.
        await RunStep("create content entry", async () =>
        {
            var body = JsonBody(BuildRequestBody("create", targetSpace!, entrySubpath, entryShortname, displayname: "selfcheck"));
            using var resp = await http.PostAsync("/managed/request", body);
            var json = await ParseAsync(resp);
            if (!resp.IsSuccessStatusCode || !IsSuccess(json))
                return (false, ExtractError(json, $"HTTP {(int)resp.StatusCode}"));
            return (true, $"{targetSpace}{entrySubpath}{entryShortname}");
        });

        // 5. Query back — search by shortname; should return exactly one record.
        await RunStep("query the entry by shortname", async () =>
        {
            var body = JsonBody(
                $"{{\"type\":\"search\",\"space_name\":\"{Esc(targetSpace!)}\",\"subpath\":\"{Esc(entrySubpath)}\"," +
                $"\"search\":\"@shortname:{Esc(entryShortname)}\",\"limit\":5,\"retrieve_json_payload\":false}}");
            using var resp = await http.PostAsync("/managed/query", body);
            var json = await ParseAsync(resp);
            if (!resp.IsSuccessStatusCode || !IsSuccess(json))
                return (false, ExtractError(json, $"HTTP {(int)resp.StatusCode}"));
            var count = json.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array
                ? recs.GetArrayLength() : 0;
            if (count != 1) return (false, $"expected 1 record, got {count}");
            return (true, "1 record");
        });

        // 6. Update — change the displayname; exercises the update branch.
        await RunStep("update the entry", async () =>
        {
            var body = JsonBody(BuildRequestBody("update", targetSpace!, entrySubpath, entryShortname, displayname: "selfcheck (updated)"));
            using var resp = await http.PostAsync("/managed/request", body);
            var json = await ParseAsync(resp);
            if (!resp.IsSuccessStatusCode || !IsSuccess(json))
                return (false, ExtractError(json, $"HTTP {(int)resp.StatusCode}"));
            return (true, null);
        });

        // 7. Cleanup — delete unless --keep. Always runs even if earlier
        //    steps failed (the entry may have been partially created), but
        //    we only count the result toward pass/fail if the operator
        //    didn't ask to keep it.
        if (!opts.Keep)
        {
            await RunStep("delete the entry", async () =>
            {
                var body = JsonBody(BuildRequestBody("delete", targetSpace!, entrySubpath, entryShortname, displayname: null));
                using var resp = await http.PostAsync("/managed/request", body);
                var json = await ParseAsync(resp);
                if (!resp.IsSuccessStatusCode || !IsSuccess(json))
                    return (false, ExtractError(json, $"HTTP {(int)resp.StatusCode}"));
                return (true, null);
            });
        }
        else
        {
            console.MarkupLine($"  [grey]·[/] [grey]delete the entry — skipped (--keep)[/]");
        }

        // 8. Logout — revokes the session row /user/login created. Confirms
        //    the revocation path is wired up. Failure here doesn't poison
        //    earlier results; the token will expire on its own anyway.
        await RunStep("logout", async () =>
        {
            using var resp = await http.PostAsync("/user/logout", JsonBody("{}"));
            var json = await ParseAsync(resp);
            if (!resp.IsSuccessStatusCode || !IsSuccess(json))
                return (false, ExtractError(json, $"HTTP {(int)resp.StatusCode}"));
            return (true, null);
        });

        return Summarize(console, sw, passed, failed, failures);
    }

    private static int Summarize(IAnsiConsole console, System.Diagnostics.Stopwatch sw,
        int passed, int failed, List<string> failures)
    {
        sw.Stop();
        var total = passed + failed;
        console.WriteLine();
        var color = failed == 0 ? "green" : "red";
        var icon  = failed == 0 ? "✓"     : "✗";
        console.MarkupLine($"[{color}]{icon}[/] [bold]{passed}/{total} checks passed[/] in {sw.Elapsed.TotalSeconds:0.0}s");
        if (failed > 0)
        {
            console.WriteLine();
            console.MarkupLine("[red]Failures:[/]");
            foreach (var f in failures) console.MarkupLine($"  [red]·[/] {Markup.Escape(f)}");
        }
        return failed == 0 ? 0 : 1;
    }

    // /managed/request body for a single Content record. The displayname is
    // optional — delete requests don't carry attributes — but every other
    // shape carries the same minimal record envelope.
    private static string BuildRequestBody(string requestType, string space, string subpath, string shortname, string? displayname)
    {
        var attrs = displayname is null
            ? "{}"
            : $"{{\"is_active\":true,\"displayname\":{{\"en\":\"{Esc(displayname)}\"}}}}";
        return $"{{\"space_name\":\"{Esc(space)}\",\"request_type\":\"{Esc(requestType)}\"," +
               $"\"records\":[{{\"resource_type\":\"content\",\"shortname\":\"{Esc(shortname)}\"," +
               $"\"subpath\":\"{Esc(subpath)}\",\"attributes\":{attrs}}}]}}";
    }

    private static SelfCheckOptions? ParseArgs(string[] args, IReadOnlyDictionary<string, string?> dotenv)
    {
        // Defaults sourced the same way curl.sh does: env vars > config.env > built-in.
        // DotEnv.Load() returns keys already projected through ToConfigurationKey
        // — LISTENING_PORT → "Dmart:ListeningPort", JWT_SECRET → "Dmart:JwtSecret",
        // etc. We lookup against that projected form, not the raw env-var name.
        var port = dotenv.TryGetValue("Dmart:ListeningPort", out var lp) && int.TryParse(lp, out var lpi) ? lpi : 5099;
        var url = Environment.GetEnvironmentVariable("DMART_URL") ?? $"http://127.0.0.1:{port}";
        var admin = Environment.GetEnvironmentVariable("DMART_ADMIN")
                    ?? (dotenv.TryGetValue("Dmart:AdminShortname", out var asn) ? asn : null)
                    ?? "dmart";
        // Password is optional — only needed when --via-login is set or
        // JWT_SECRET is unavailable. Resolve lazily.
        string? pwd = Environment.GetEnvironmentVariable("DMART_PWD")
                  ?? (dotenv.TryGetValue("Dmart:AdminPassword", out var ap) ? ap : null);
        string? space = null;
        var subpath = "/";
        var keep = false;
        var verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    PrintHelp();
                    return null;
                case "--url"      when i + 1 < args.Length: url    = args[++i]; break;
                case "--admin"    when i + 1 < args.Length: admin  = args[++i]; break;
                case "--password" when i + 1 < args.Length: pwd    = args[++i]; break;
                case "--password-stdin":
                    pwd = Console.In.ReadLine() ?? pwd;
                    break;
                case "--space"    when i + 1 < args.Length: space   = args[++i]; break;
                case "--subpath"  when i + 1 < args.Length: subpath = args[++i]; break;
                case "--keep":     keep     = true; break;
                case "-v":
                case "--verbose":  verbose  = true; break;
                default:
                    Console.Error.WriteLine($"selfcheck: unknown argument '{args[i]}'");
                    PrintHelp();
                    return null;
            }
        }
        return new SelfCheckOptions(url, admin, pwd, space, subpath, keep, verbose);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            dmart selfcheck — operator-facing smoke against a running dmart server.

            Usage: dmart selfcheck [options]

            Prerequisite: the admin must have a password. After `dmart init`
            and `dmart migrate`, set one with `dmart passwd dmart <pwd>` or
            export ADMIN_PASSWORD=... in config.env (then restart dmart).

            Options:
              --url <url>            Server URL (default: http://127.0.0.1:<LISTENING_PORT>)
              --admin <shortname>    Admin shortname (default: ADMIN_SHORTNAME from config.env or 'dmart')
              --password <pwd>       Admin password (default: ADMIN_PASSWORD from config.env, or DMART_PWD env)
              --password-stdin       Read password from stdin (one line)
              --space <name>         Space to CRUD against (default: first non-management space)
              --subpath <path>       Subpath under the space for the test entry (default: '/')
                                     Some spaces don't allow content at root — set to e.g. '/reports'
              --keep                 Skip the cleanup-delete step
              -v, --verbose          Show extra detail per step
              -h, --help             This help

            Env vars (read before config.env): DMART_URL, DMART_ADMIN, DMART_PWD.
            Exit code: 0 on full pass, 1 on any failure.
            """);
    }

    // ---- Small JSON / HTTP helpers, same shape as Cli/DmartClient.cs ----

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ParseAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return default;
        try { return JsonDocument.Parse(text).RootElement; }
        catch { return default; }
    }

    private static bool IsSuccess(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty("status", out var st) &&
        st.GetString() == "success";

    private static string ExtractError(JsonElement json, string fallback)
    {
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty("error", out var err) &&
            err.ValueKind == JsonValueKind.Object &&
            err.TryGetProperty("message", out var m))
        {
            return m.GetString() ?? fallback;
        }
        return fallback;
    }

    // Same JSON-string escaper Cli/DmartClient.cs uses — handles the four
    // characters that break literal-string JSON building. Values flowing
    // through here are operator-supplied (URL, shortname, password, space),
    // so we accept conservative double-escaping over allowing a bad literal.
    private static string Esc(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r");
}
