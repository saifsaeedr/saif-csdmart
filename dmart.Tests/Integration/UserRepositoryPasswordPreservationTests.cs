using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Regression tests for PR #68: an upsert with Password=null must NOT wipe the
// stored hash. Both upsert paths on UserRepository carry the same `password =
// COALESCE(EXCLUDED.password, users.password)` ON CONFLICT clause, so this
// fixture pins the invariant on both — a future refactor that drops the
// COALESCE on either site fails the matching test, not silently corrupting
// credentials in production.
//
// Real-world trigger: a partial-update flow (a plugin callback updating
// profile fields, or a REST handler patching email) that loads a User,
// flips one field, and re-upserts without explicitly carrying Password
// forward. Pre-fix that wiped the hash; post-fix the stored value is
// preserved.
public sealed class UserRepositoryPasswordPreservationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserRepositoryPasswordPreservationTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task UpsertAsync_With_Null_Password_Preserves_Existing_Hash()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var sn = "pwd_" + Guid.NewGuid().ToString("N")[..10];
        var originalHash = hasher.Hash("OriginalPass1");

        var initial = MakeUser(sn, originalHash);
        await users.UpsertAsync(initial);
        try
        {
            // Re-upsert the SAME user record but with Password explicitly null —
            // the scenario a caller hits when they mutate a non-password field
            // (e.g. Email or Displayname) and re-save without carrying the
            // password forward.
            var without = initial with { Password = null, Email = $"{sn}@updated.test" };
            await users.UpsertAsync(without);

            var reloaded = await users.GetByShortnameAsync(sn);
            reloaded.ShouldNotBeNull();
            reloaded!.Password.ShouldBe(originalHash,
                "UpsertAsync must COALESCE the password column on ON CONFLICT — a null password from the caller should leave the stored hash untouched.");
            reloaded.Email.ShouldBe($"{sn}@updated.test",
                "the non-password mutation must still apply.");
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task UpsertWithPriorAsync_With_Null_Password_Preserves_Existing_Hash()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var sn = "pwdp_" + Guid.NewGuid().ToString("N")[..10];
        var originalHash = hasher.Hash("OriginalPass2");

        await users.UpsertAsync(MakeUser(sn, originalHash));
        try
        {
            var loaded = await users.GetByShortnameAsync(sn);
            loaded.ShouldNotBeNull();

            // The plugin-callback path (EmitUpdateUser) reaches the
            // password column through UpsertWithPriorAsync. A plugin that
            // mutates profile fields and forgets to carry the password
            // hash forward should not be able to wipe credentials.
            var mutated = loaded! with { Password = null, Displayname = new("UPDATED") };
            var (prior, inserted) = await users.UpsertWithPriorAsync(mutated);
            inserted.ShouldBeFalse("we expected an update, not an insert.");
            prior.ShouldNotBeNull();
            prior!.Password.ShouldBe(originalHash, "prior should still reflect the original hash.");

            var reloaded = await users.GetByShortnameAsync(sn);
            reloaded.ShouldNotBeNull();
            reloaded!.Password.ShouldBe(originalHash,
                "UpsertWithPriorAsync must COALESCE the password column on ON CONFLICT.");
            reloaded.Displayname!.En.ShouldBe("UPDATED",
                "the non-password mutation must still apply.");
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task UpsertAsync_With_NonNull_Password_Still_Overwrites_Hash()
    {
        // The COALESCE only takes effect when EXCLUDED.password IS NULL.
        // Pin the legitimate rotation path: a non-null hash from the caller
        // must replace the stored value (otherwise `dmart passwd` and the
        // password-reset flow break).
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var sn = "pwdrot_" + Guid.NewGuid().ToString("N")[..10];
        var originalHash = hasher.Hash("OriginalRotated");
        var rotatedHash = hasher.Hash("AfterRotation");

        await users.UpsertAsync(MakeUser(sn, originalHash));
        try
        {
            var updated = MakeUser(sn, rotatedHash);
            await users.UpsertAsync(updated);

            var reloaded = await users.GetByShortnameAsync(sn);
            reloaded.ShouldNotBeNull();
            reloaded!.Password.ShouldBe(rotatedHash,
                "a non-null password in the upsert payload must overwrite the stored hash — COALESCE only fires when EXCLUDED.password IS NULL.");
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    private static User MakeUser(string shortname, string passwordHash) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        IsActive = true,
        Password = passwordHash,
        Email = $"{shortname}@test.local",
        IsEmailVerified = true,
        Roles = new(),
        Groups = new(),
        Type = UserType.Web,
        Language = Language.En,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
