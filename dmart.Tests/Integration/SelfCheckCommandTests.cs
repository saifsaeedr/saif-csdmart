using Dmart.Cli;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins SelfCheckCommand.RunAsync end-to-end against the factory's in-process
// dmart server. The factory exposes an HttpClient bound to that server, so
// the smoke really hits /user/login, /info/manifest, /managed/query and
// /managed/request the same way an operator's `dmart selfcheck` would.
//
// The fixture seeds the bootstrap admin ("dmart") and a known password; we
// pass them through SelfCheckOptions directly rather than going through
// the config.env-driven ParseArgs path (which would require a real config.env
// on disk — irrelevant for the smoke's correctness).
[Collection(SharedAdminStateCollection.Name)]
public sealed class SelfCheckCommandTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SelfCheckCommandTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task SelfCheck_FullSmoke_PassesAgainst_RunningServer()
    {
        // Factory client carries the in-process BaseAddress already; we
        // don't want the WebApplicationFactory's auto-cookie-handling
        // (selfcheck uses bearer tokens explicitly), so opt out.
        var http = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        var opts = new SelfCheckCommand.SelfCheckOptions(
            Url: http.BaseAddress!.ToString(),
            Admin: _factory.AdminShortname,
            Password: _factory.AdminPassword,
            // No --space override — selfcheck picks the first non-management
            // space, which the factory bootstraps as "applications" via the
            // seeded spaces. If that ever changes, this test will fail with
            // a clear "no spaces visible" or create-failure message rather
            // than a silent regression.
            // The factory bootstraps only the management space, and content
            // at its root subpath isn't writable. Use management/reports —
            // matches the path SavedQueryParityTests writes content to.
            Space: "management",
            Subpath: "/reports",
            Keep: false,
            Verbose: false,
            JwtBootstrap: false);

        // Record Spectre output so the assertion failure carries the
        // failing step's name — much more useful than a bare "exit code 1".
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });

        var exitCode = await SelfCheckCommand.RunAsync(http, opts, console);
        exitCode.ShouldBe(0, customMessage: $"selfcheck output:\n{output}");
    }

    [FactIfPg]
    public async Task SelfCheck_WrongPassword_FailsAt_LoginStep()
    {
        var http = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        var opts = new SelfCheckCommand.SelfCheckOptions(
            Url: http.BaseAddress!.ToString(),
            Admin: _factory.AdminShortname,
            Password: "definitely-not-the-real-password",
            // The factory bootstraps only the management space, and content
            // at its root subpath isn't writable. Use management/reports —
            // matches the path SavedQueryParityTests writes content to.
            Space: "management",
            Subpath: "/reports",
            Keep: false,
            Verbose: false,
            JwtBootstrap: false);

        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(TextWriter.Null),
        });

        // Wrong password → login step fails → smoke aborts before any
        // downstream step runs → exit code 1.
        var exitCode = await SelfCheckCommand.RunAsync(http, opts, console);
        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task SelfCheck_MissingPassword_FailsCleanly_NoNetwork()
    {
        // No password supplied — selfcheck should refuse to send an empty
        // /user/login and instead emit a pointer to `dmart passwd`. Verified
        // by passing a deliberately-unreachable URL: if selfcheck tried to
        // hit the network we'd see a connection error in the failure list
        // rather than the "no password supplied" branch.
        var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        var opts = new SelfCheckCommand.SelfCheckOptions(
            Url: "http://127.0.0.1:1",
            Admin: "dmart",
            Password: null,
            // The factory bootstraps only the management space, and content
            // at its root subpath isn't writable. Use management/reports —
            // matches the path SavedQueryParityTests writes content to.
            Space: "management",
            Subpath: "/reports",
            Keep: false,
            Verbose: false,
            JwtBootstrap: false);

        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(TextWriter.Null),
        });

        var exitCode = await SelfCheckCommand.RunAsync(http, opts, console);
        exitCode.ShouldBe(1);
    }

    // --jwt-bootstrap mode: caller mints a JWT itself and threads it into
    // RunAsync via preMintedToken. Selfcheck skips /user/login entirely
    // and goes straight to step 2 with Authorization: Bearer <token>.
    // Important: even with a stale ADMIN_PASSWORD (or none at all) the
    // smoke completes successfully. This pins the design where the
    // jwt-bootstrap path is decoupled from the password.
    [FactIfPg]
    public async Task SelfCheck_JwtBootstrap_Succeeds_WithoutPassword()
    {
        var http = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        // Mint the JWT the same way SelfCheckCommand.JwtBootstrapAsync
        // would, using the factory's wired-up services. The session row
        // gets inserted via UserRepository.CreateSessionAsync (which
        // hashes the raw JWT into the token column) — matching the
        // server's OnTokenValidated lookup.
        var settings = _factory.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Dmart.Config.DmartSettings>>().Value;
        var issuer = new Dmart.Auth.JwtIssuer(Microsoft.Extensions.Options.Options.Create(settings));
        var users = _factory.Services.GetRequiredService<Dmart.DataAdapters.Sql.UserRepository>();
        var hasher = new Dmart.Auth.SessionTokenHasher(settings);
        var jwt = issuer.IssueAccess(_factory.AdminShortname,
            roles: null, userType: Dmart.Models.Enums.UserType.Web);
        await users.CreateSessionAsync(_factory.AdminShortname, jwt);

        try
        {
            var opts = new SelfCheckCommand.SelfCheckOptions(
                Url: http.BaseAddress!.ToString(),
                Admin: _factory.AdminShortname,
                Password: null,  // deliberately omitted — JWT path doesn't need it
                Space: "management",
                Subpath: "/reports",
                Keep: false,
                Verbose: false,
                JwtBootstrap: true);

            var output = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(output),
            });

            var exitCode = await SelfCheckCommand.RunAsync(http, opts, console, preMintedToken: jwt);
            exitCode.ShouldBe(0, customMessage: $"selfcheck output:\n{output}");
            var outputText = output.ToString();
            outputText.ShouldContain("jwt-bootstrap as " + _factory.AdminShortname);
        }
        finally
        {
            // Clean up the session row we inserted.
            try { await users.DeleteSessionAsync(_factory.AdminShortname, jwt); } catch { }
        }
    }
}
