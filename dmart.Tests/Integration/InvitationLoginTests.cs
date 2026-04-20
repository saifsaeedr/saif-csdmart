using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Invitation-login (Python /user/login PATH A) integration tests.
// Each test provisions its own dedicated user so cross-class test
// parallelism can't race on a shared admin row.
public sealed class InvitationLoginTests : IClassFixture<DmartFactory>, IAsyncLifetime
{
    private readonly DmartFactory _factory;
    public InvitationLoginTests(DmartFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        // no-op — each test provisions its own user
        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Task.CompletedTask;

    [FactIfPg]
    public async Task ValidInvitation_SucceedsAndSetsForcePasswordChange()
    {
        var (shortname, _) = await CreateUserAsync(email: true);
        var token = await MintAsync(shortname, InvitationChannel.Email);

        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(null, null, null, null, token),
                DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body.ShouldNotBeNull();
            body!.Status.ShouldBe(Status.Success);
            body.Records![0].Attributes!.ContainsKey("access_token").ShouldBeTrue();

            var users = _factory.Services.GetRequiredService<UserRepository>();
            var refreshed = await users.GetByShortnameAsync(shortname);
            refreshed.ShouldNotBeNull();
            refreshed!.ForcePasswordChange.ShouldBeTrue();
            refreshed.IsEmailVerified.ShouldBeTrue();
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task ReusedInvitation_Fails()
    {
        var (shortname, _) = await CreateUserAsync(email: true);
        var token = await MintAsync(shortname, InvitationChannel.Email);

        try
        {
            var client = _factory.CreateClient();
            var login = new UserLoginRequest(null, null, null, null, token);

            var first = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            first.StatusCode.ShouldBe(HttpStatusCode.OK);

            var second = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            second.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task UnknownTokenNotInDb_Fails()
    {
        // Mint a JWT but DON'T persist the row — invitation login requires
        // the DB row, so this should fail with INVALID_INVITATION.
        var (shortname, _) = await CreateUserAsync(email: true);
        try
        {
            var jwt = _factory.Services.GetRequiredService<InvitationJwt>();
            var token = jwt.Mint(shortname, InvitationChannel.Email);

            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(null, null, null, null, token),
                DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task TamperedToken_Fails()
    {
        var (shortname, _) = await CreateUserAsync(email: true);
        var token = await MintAsync(shortname, InvitationChannel.Email);
        var parts = token.Split('.');
        var tampered = parts[0] + "." + parts[1] + "." + (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];

        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(null, null, null, null, tampered),
                DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task IdentityMismatch_Fails()
    {
        var (shortname, _) = await CreateUserAsync(email: true);
        var token = await MintAsync(shortname, InvitationChannel.Email);

        try
        {
            // Supply a shortname in the body that doesn't match the JWT's claim.
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest("definitely_not_this_user", null, null, null, token),
                DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            // Invitation row MUST still exist — identity mismatch shouldn't consume it.
            var invitations = _factory.Services.GetRequiredService<InvitationRepository>();
            (await invitations.GetValueAsync(token)).ShouldNotBeNull();
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task SetPasswordClearsForcePasswordChange()
    {
        var (shortname, _) = await CreateUserAsync(email: true, password: "OldPass1234!");
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var svc = _factory.Services.GetRequiredService<UserService>();

            // Stage: pretend the user was just invitation-logged-in.
            var u = (await users.GetByShortnameAsync(shortname))!;
            await users.UpsertAsync(u with { ForcePasswordChange = true, UpdatedAt = DateTime.UtcNow });

            var patch = new Dictionary<string, object> { ["password"] = "NewPass1234!" };
            var result = await svc.UpdateProfileAsync(shortname, patch);
            result.IsOk.ShouldBeTrue(result.ErrorMessage);

            var after = await users.GetByShortnameAsync(shortname);
            after!.ForcePasswordChange.ShouldBeFalse();
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    // ---- helpers ----

    // Creates a throwaway user for a single test. Returns shortname + email.
    private async Task<(string Shortname, string Email)> CreateUserAsync(bool email, string? password = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"inv_test_{suffix}";
        var address = $"{shortname}@test.local";

        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = email ? address : null,
            Password = password is null ? null : hasher.Hash(password),
            Roles = new() { },
            Groups = new(),
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user);
        return (shortname, address);
    }

    private async Task<string> MintAsync(string shortname, InvitationChannel channel)
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var svc = _factory.Services.GetRequiredService<InvitationService>();
        var user = (await users.GetByShortnameAsync(shortname))!;
        var t = await svc.MintAsync(user, channel);
        t.ShouldNotBeNull();
        return t!;
    }

    private async Task DeleteUserAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);
        }
        catch { /* best-effort cleanup */ }
    }
}
