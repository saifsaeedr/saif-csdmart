using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// OTP codes are stored hashed (keyed HMAC via OtpHasher), never as the raw
// 6-digit code. A DB read must not surface a live, replayable credential. These
// tests pin the at-rest representation AND that verification still round-trips.
public sealed class OtpHashingTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpHashingTests(DmartFactory factory) => _factory = factory;

    private OtpRepository Repo() => _factory.Services.GetRequiredService<OtpRepository>();
    private OtpHasher Hasher() => _factory.Services.GetRequiredService<OtpHasher>();

    // Reads the raw HSTORE `code` field straight from the table, bypassing the
    // repository — this is what an attacker with DB read access would see.
    private async Task<string?> RawStoredCodeAsync(string key)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT value -> 'code' FROM otp WHERE key = $1", conn);
        cmd.Parameters.Add(new() { Value = key });
        var raw = await cmd.ExecuteScalarAsync();
        return raw is null or DBNull ? null : (string)raw;
    }

    [FactIfPg]
    public async Task StoreAsync_Persists_Hashed_Code_Not_Plaintext()
    {
        var key = $"otphash_{Guid.NewGuid():N}@x.y";
        const string code = "123456";
        try
        {
            await Repo().StoreAsync(key, code, DateTime.UtcNow.AddMinutes(5));

            var stored = await RawStoredCodeAsync(key);
            stored.ShouldNotBeNull();
            stored.ShouldNotBe(code, "the raw 6-digit code must never be persisted");
            stored.ShouldBe(Hasher().Hash(code), "the stored value is the keyed HMAC of the code");
        }
        finally { await Repo().DeleteAsync(key); }
    }

    [FactIfPg]
    public async Task VerifyAndConsume_Succeeds_With_Correct_Code_And_Deletes_Row()
    {
        var key = $"otphash_{Guid.NewGuid():N}@x.y";
        const string code = "654321";
        try
        {
            await Repo().StoreAsync(key, code, DateTime.UtcNow.AddMinutes(5));

            (await Repo().VerifyAndConsumeAsync(key, code)).ShouldBeTrue();
            (await RawStoredCodeAsync(key)).ShouldBeNull("a consumed OTP row must be deleted");
        }
        finally { await Repo().DeleteAsync(key); }
    }

    [FactIfPg]
    public async Task VerifyAndConsume_Fails_With_Wrong_Code()
    {
        var key = $"otphash_{Guid.NewGuid():N}@x.y";
        try
        {
            await Repo().StoreAsync(key, "111111", DateTime.UtcNow.AddMinutes(5));
            (await Repo().VerifyAndConsumeAsync(key, "222222")).ShouldBeFalse();
        }
        finally { await Repo().DeleteAsync(key); }
    }

    [FactIfPg]
    public async Task VerifyPeek_Matches_Correct_Code_Without_Consuming()
    {
        var key = $"otphash_{Guid.NewGuid():N}@x.y";
        const string code = "909090";
        try
        {
            await Repo().StoreAsync(key, code, DateTime.UtcNow.AddMinutes(5));

            (await Repo().VerifyPeekAsync(key, "000000")).ShouldBeFalse();
            (await Repo().VerifyPeekAsync(key, code)).ShouldBeTrue();
            // Peek must NOT consume — the row is still verifiable afterwards.
            (await RawStoredCodeAsync(key)).ShouldNotBeNull("peek must not delete the row");
        }
        finally { await Repo().DeleteAsync(key); }
    }

    [FactIfPg]
    public async Task VerifyPeek_Returns_False_For_Missing_Key()
    {
        (await Repo().VerifyPeekAsync($"otphash_missing_{Guid.NewGuid():N}", "123456"))
            .ShouldBeFalse();
    }
}
