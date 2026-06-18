using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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

// Reproduces the reported bug: /managed/request {create,update} (and /user/create)
// for the non-entry "principal" types (User/Role/Group/Permission/Space) did NOT
// validate payload.body against payload.schema_shortname. Schema validation only
// ran inside EntryService, which handles entry-type resources; the principal types
// go through dedicated repos (UserRepository, AccessRepository, ...) and bypassed it.
//
// Python validates uniformly for every resource type in serve_request_create /
// serve_request_update (backend/api/managed/utils.py), so this is a parity gap.
//
// These tests pin the User path (the one a principal type carries a schema-validated
// payload on in practice — the user profile). The fix routes the same
// SchemaValidator gate EntryService already applies through the principal paths.
public sealed class PayloadSchemaValidationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PayloadSchemaValidationTests(DmartFactory factory) => _factory = factory;

    // Strict schema: requires an integer `age`, forbids any other property.
    private const string AgeSchema =
        "{\"type\":\"object\",\"properties\":{\"age\":{\"type\":\"integer\"}}," +
        "\"required\":[\"age\"],\"additionalProperties\":false}";

    // Seed a schema entry in the management space at /schema (where SchemaValidator
    // looks). Done via the repository so the test doesn't depend on the create path
    // it's about to exercise.
    private async Task<string> SeedSchemaAsync()
    {
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var shortname = $"itestschema{Guid.NewGuid():N}"[..20];
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/schema",
            ResourceType = ResourceType.Schema,
            OwnerShortname = "dmart",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(AgeSchema).RootElement.Clone(),
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return shortname;
    }

    private static string CreateUserBody(string shortname, string schema, string bodyJson) =>
        "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
        "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
        "\"attributes\":{\"is_active\":true,\"payload\":{\"content_type\":\"json\"," +
        "\"schema_shortname\":\"" + schema + "\",\"body\":" + bodyJson + "}}}]}";

    // Service-level behavioral check of the shared gate the fix routes every
    // principal path through. Boots via DI only (no HTTP host), so it verifies
    // the gate end-to-end against a real DB-backed schema lookup.
    [FactIfPg]
    public async Task SchemaValidator_Gate_Rejects_Invalid_Body_And_Accepts_Valid()
    {
        var schema = await SeedSchemaAsync();
        var schemas = _factory.Services.GetRequiredService<SchemaValidator>();

        var bad = new Payload
        {
            ContentType = ContentType.Json,
            SchemaShortname = schema,
            Body = JsonDocument.Parse("{\"age\":\"not-an-integer\"}").RootElement.Clone(),
        };
        var badResult = await schemas.ValidatePayloadAsync("management", ResourceType.User, bad);
        badResult.ShouldNotBeNull("a schema-violating body must produce an error");
        badResult!.ShouldContain("payload failed schema validation");

        var good = new Payload
        {
            ContentType = ContentType.Json,
            SchemaShortname = schema,
            Body = JsonDocument.Parse("{\"age\":30}").RootElement.Clone(),
        };
        var goodResult = await schemas.ValidatePayloadAsync("management", ResourceType.User, good);
        goodResult.ShouldBeNull("a schema-conforming body must pass");
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Schema_Violating_Payload_Is_Rejected()
    {
        var schema = await SeedSchemaAsync();
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestu{Guid.NewGuid():N}"[..14];
        try
        {
            // age is a string, not an integer → violates the schema.
            var body = CreateUserBody(shortname, schema, "{\"age\":\"not-an-integer\"}");
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Failed,
                $"create with schema-violating payload must be rejected; got: {raw}");
            raw.ShouldContain("payload failed schema validation");
            (await users.GetByShortnameAsync(shortname))
                .ShouldBeNull("user must not be persisted when its payload fails schema validation");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Valid_Payload_Succeeds()
    {
        // Control: a schema-conforming payload must still be accepted, so the fix
        // rejects only genuine violations rather than gating every payload.
        var schema = await SeedSchemaAsync();
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestu{Guid.NewGuid():N}"[..14];
        try
        {
            var body = CreateUserBody(shortname, schema, "{\"age\":30}");
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Success, $"valid payload must be accepted; got: {raw}");
            (await users.GetByShortnameAsync(shortname)).ShouldNotBeNull();
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Update_User_With_Schema_Violating_Payload_Is_Rejected()
    {
        var schema = await SeedSchemaAsync();
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestu{Guid.NewGuid():N}"[..14];
        try
        {
            // Seed an existing user (valid/empty payload) directly, then update it
            // with a schema-violating payload via /managed/request.
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = "dmart",
                IsActive = true,
                Language = Language.En,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var body =
                "{\"space_name\":\"management\",\"request_type\":\"update\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\"," +
                "\"schema_shortname\":\"" + schema + "\",\"body\":{\"age\":\"not-an-integer\"}}}}]}";
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Failed,
                $"update with schema-violating payload must be rejected; got: {raw}");
            raw.ShouldContain("payload failed schema validation");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Update_User_With_Valid_Payload_Succeeds()
    {
        // Control: a schema-conforming update must still be accepted, so the gate
        // rejects only genuine violations rather than blocking every user update.
        var schema = await SeedSchemaAsync();
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestu{Guid.NewGuid():N}"[..14];
        try
        {
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = "dmart",
                IsActive = true,
                Language = Language.En,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var body =
                "{\"space_name\":\"management\",\"request_type\":\"update\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\"," +
                "\"schema_shortname\":\"" + schema + "\",\"body\":{\"age\":42}}}}]}";
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Success, $"valid update payload must be accepted; got: {raw}");

            // The patch-declared schema must actually be persisted on the merged
            // payload (MergeBody honoring schema_shortname), and the body applied.
            var stored = await users.GetByShortnameAsync(shortname);
            stored.ShouldNotBeNull();
            stored!.Payload.ShouldNotBeNull();
            stored.Payload!.SchemaShortname.ShouldBe(schema);
            stored.Payload!.Body!.Value.GetProperty("age").GetInt32().ShouldBe(42);
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
