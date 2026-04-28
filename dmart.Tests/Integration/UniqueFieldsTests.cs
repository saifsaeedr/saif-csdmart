using System;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Folder-level uniqueness (port of dmart/backend/data_adapters/sql/
// adapter.py::validate_uniqueness). The folder under which an entry is
// being created/updated can declare a `payload.body.unique_fields` list
// of compound keys; EntryService.CreateAsync and UpdateAsync now check
// each compound and reject the write with DATA_SHOULD_BE_UNIQUE on
// collision.
public class UniqueFieldsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UniqueFieldsTests(DmartFactory factory) => _factory = factory;

    private (SpaceRepository spaces, EntryRepository entries, EntryService entryService) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<SpaceRepository>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<EntryService>());
    }

    private async Task<string> SeedSpaceWithFolderAsync(SpaceRepository spaces, EntryRepository entries, string uniqueFieldsJson)
    {
        var spaceName = $"uniq_{Guid.NewGuid():N}"[..16];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        // Folder lives at subpath="/" with shortname="people"; entries written
        // under subpath="/people" pull their unique_fields from this folder's
        // payload.body (mirrors the os.path.split walk in Python).
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "people",
            SpaceName = spaceName,
            Subpath = "/",
            ResourceType = ResourceType.Folder,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse($$"""{"unique_fields": {{uniqueFieldsJson}}}""").RootElement.Clone(),
            },
        });

        return spaceName;
    }

    private static Entry MakeContent(string space, string subpath, string shortname, object body)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Content,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.Clone(),
            },
        };

    [FactIfPg]
    public async Task Scalar_Field_Create_Rejects_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[["payload.body.email"]]""");
        try
        {
            var first = await entryService.CreateAsync(
                MakeContent(space, "/people", "alice", new { email = "a@x.com" }), "dmart");
            first.IsOk.ShouldBeTrue($"first create failed: {first.ErrorMessage}");

            var second = await entryService.CreateAsync(
                MakeContent(space, "/people", "alice2", new { email = "a@x.com" }), "dmart");
            second.IsOk.ShouldBeFalse("colliding create should be rejected");
            second.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
            second.ErrorType.ShouldBe(ErrorTypes.Request);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task Scalar_Field_Update_Allows_Self_And_Rejects_Other_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[["payload.body.email"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "alice", new { email = "a@x.com" }), "dmart"))
                .IsOk.ShouldBeTrue();
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "bob", new { email = "b@x.com" }), "dmart"))
                .IsOk.ShouldBeTrue();

            // Update bob to keep his own email — must not flag self-collision.
            var selfPatch = new System.Collections.Generic.Dictionary<string, object>
            {
                ["payload"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse("""{"email": "b@x.com"}""").RootElement.Clone(),
                },
            };
            var sameEmailUpdate = await entryService.UpdateAsync(
                new Locator(ResourceType.Content, space, "/people", "bob"), selfPatch, "dmart");
            sameEmailUpdate.IsOk.ShouldBeTrue($"self-email update should pass: {sameEmailUpdate.ErrorMessage}");

            // Update bob to alice's email — must collide.
            var stealEmail = new System.Collections.Generic.Dictionary<string, object>
            {
                ["payload"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse("""{"email": "a@x.com"}""").RootElement.Clone(),
                },
            };
            var collide = await entryService.UpdateAsync(
                new Locator(ResourceType.Content, space, "/people", "bob"), stealEmail, "dmart");
            collide.IsOk.ShouldBeFalse("update onto another row's value should be rejected");
            collide.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task ListOfStrings_Field_Rejects_Any_Element_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[["payload.body.ids"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "first", new { ids = new[] { "a", "b" } }), "dmart"))
                .IsOk.ShouldBeTrue();

            // Overlap on "b" — second create must be rejected.
            var overlap = await entryService.CreateAsync(
                MakeContent(space, "/people", "second", new { ids = new[] { "b", "c" } }), "dmart");
            overlap.IsOk.ShouldBeFalse("overlapping list element should be rejected");
            overlap.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);

            // Disjoint ids — must succeed.
            var disjoint = await entryService.CreateAsync(
                MakeContent(space, "/people", "third", new { ids = new[] { "c", "d" } }), "dmart");
            disjoint.IsOk.ShouldBeTrue($"disjoint ids should pass: {disjoint.ErrorMessage}");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task ObjectArray_Field_Indexed_With_Bracket_Sub_Rejects_Element_Collision()
    {
        // Paths can iterate object arrays via `[].sub`. Here `variants` is
        // an array of {sku, price} objects; we want every sku across the
        // folder to be unique.
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[["payload.body.variants[].sku"]]""");
        try
        {
            var first = await entryService.CreateAsync(
                MakeContent(space, "/people", "p1", new
                {
                    variants = new object[]
                    {
                        new { sku = "SKU-A", price = 10 },
                        new { sku = "SKU-B", price = 20 },
                    },
                }), "dmart");
            first.IsOk.ShouldBeTrue($"first create failed: {first.ErrorMessage}");

            // Overlap on SKU-B → second create must be rejected.
            var overlap = await entryService.CreateAsync(
                MakeContent(space, "/people", "p2", new
                {
                    variants = new object[]
                    {
                        new { sku = "SKU-B", price = 30 },
                        new { sku = "SKU-C", price = 40 },
                    },
                }), "dmart");
            overlap.IsOk.ShouldBeFalse("overlap on .sku should be rejected");
            overlap.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);

            // Disjoint skus → must succeed.
            var disjoint = await entryService.CreateAsync(
                MakeContent(space, "/people", "p3", new
                {
                    variants = new object[]
                    {
                        new { sku = "SKU-D", price = 50 },
                        new { sku = "SKU-E", price = 60 },
                    },
                }), "dmart");
            disjoint.IsOk.ShouldBeTrue($"disjoint skus should pass: {disjoint.ErrorMessage}");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task No_UniqueFields_On_Folder_Allows_Any_Duplicate()
    {
        var (spaces, entries, entryService) = Resolve();
        // Folder body has no unique_fields → validator returns Ok unconditionally.
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "x1", new { email = "dup@x.com" }), "dmart"))
                .IsOk.ShouldBeTrue();
            var twin = await entryService.CreateAsync(
                MakeContent(space, "/people", "x2", new { email = "dup@x.com" }), "dmart");
            twin.IsOk.ShouldBeTrue("no unique_fields → duplicates allowed");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    // Regression: a multi-path compound `["email", "phone"]` must keep
    // probing for collisions even when ONE of the paths is unchanged on
    // update. Earlier C# port skipped the WHOLE compound on first
    // unchanged path and silently allowed duplicates that share an
    // unchanged field with an existing row whose changed field matches.
    [FactIfPg]
    public async Task MultiPath_Update_With_One_Field_Unchanged_Still_Collides()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries,
            """[["payload.body.email", "payload.body.phone"]]""");
        try
        {
            // Two existing entries that don't collide today.
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "alice",
                    new { email = "shared@x.com", phone = "111" }), "dmart"))
                .IsOk.ShouldBeTrue();
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "bob",
                    new { email = "shared@x.com", phone = "222" }), "dmart"))
                .IsOk.ShouldBeTrue();

            // Update bob to keep his email but change his phone to alice's.
            // Email is UNCHANGED, phone is CHANGING — the new compound
            // ("shared@x.com", "111") collides with alice. Must reject.
            var patch = new System.Collections.Generic.Dictionary<string, object>
            {
                ["payload"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse("""{"email":"shared@x.com","phone":"111"}""")
                                          .RootElement.Clone(),
                },
            };
            var collide = await entryService.UpdateAsync(
                new Locator(ResourceType.Content, space, "/people", "bob"), patch, "dmart");
            collide.IsOk.ShouldBeFalse("compound collision must be detected even when one field is unchanged");
            collide.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    // Compound semantics: a single-path match (sharing email but not phone)
    // must NOT count as a collision — the whole compound has to match.
    [FactIfPg]
    public async Task MultiPath_Single_Path_Match_Is_Not_A_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries,
            """[["payload.body.email", "payload.body.phone"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "alice",
                    new { email = "shared@x.com", phone = "111" }), "dmart"))
                .IsOk.ShouldBeTrue();

            // Same email, different phone — compound differs, so no collision.
            var partialOverlap = await entryService.CreateAsync(
                MakeContent(space, "/people", "bob",
                    new { email = "shared@x.com", phone = "222" }), "dmart");
            partialOverlap.IsOk.ShouldBeTrue(
                $"single-path overlap on multi-path compound must not collide: {partialOverlap.ErrorMessage}");

            // Same phone, different email — same story.
            var phoneOverlap = await entryService.CreateAsync(
                MakeContent(space, "/people", "carol",
                    new { email = "different@x.com", phone = "111" }), "dmart");
            phoneOverlap.IsOk.ShouldBeTrue(
                $"single-path phone overlap must not collide: {phoneOverlap.ErrorMessage}");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    // Pins a known limitation: paths with TWO `[]` segments
    // (`outer[].inner[].leaf`) are extracted correctly by the validator's
    // WalkPath but their SQL probe is generated by
    // QueryHelper.BuildPayloadArraySql, which only handles a single `[]`
    // segment per path. The second `[]` becomes part of a literal key
    // name and the EXISTS predicate matches nothing. Result: no collision
    // is detected for nested-array compounds today.
    //
    // Flip the assertions when QueryHelper grows nested-EXISTS support.
    [FactIfPg]
    public async Task NestedArrays_Search_Is_Single_Bracket_Only()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries,
            """[["payload.body.outer[].inner[].leaf"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "p1", new
                {
                    outer = new object[]
                    {
                        new { inner = new object[] { new { leaf = "alpha" }, new { leaf = "beta" } } },
                    },
                }), "dmart"))
                .IsOk.ShouldBeTrue();

            // p2 reuses "alpha" deep inside its nested structure. With
            // single-`[]`-only SQL support this is NOT detected today.
            var notYetCaught = await entryService.CreateAsync(
                MakeContent(space, "/people", "p2", new
                {
                    outer = new object[]
                    {
                        new { inner = new object[] { new { leaf = "gamma" } } },
                        new { inner = new object[] { new { leaf = "alpha" } } },
                    },
                }), "dmart");
            // Expected to PASS today (the SQL probe generated for a
            // double-bracket path matches nothing). Flip to ShouldBeFalse
            // + DATA_SHOULD_BE_UNIQUE once QueryHelper handles nested `[]`.
            notYetCaught.IsOk.ShouldBeTrue(
                "nested-array compound currently degenerates to a no-op probe — see WalkPath's doc comment");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    // Update on an array-of-object compound. Confirms that the per-path
    // self-filter and the parity skip-rules behave correctly when the diff
    // lives inside an array element rather than a flat scalar field.
    [FactIfPg]
    public async Task ObjectArray_Update_Allows_Self_And_Rejects_Other_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries,
            """[["payload.body.variants[].sku"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "p1", new
                {
                    variants = new object[] { new { sku = "A" }, new { sku = "B" } },
                }), "dmart"))
                .IsOk.ShouldBeTrue();
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "p2", new
                {
                    variants = new object[] { new { sku = "X" }, new { sku = "Y" } },
                }), "dmart"))
                .IsOk.ShouldBeTrue();

            // Update p2 to keep its own variants — must NOT flag self-collision.
            var selfPatch = new System.Collections.Generic.Dictionary<string, object>
            {
                ["payload"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        variants = new object[] { new { sku = "X" }, new { sku = "Y" } },
                    })).RootElement.Clone(),
                },
            };
            var sameVariants = await entryService.UpdateAsync(
                new Locator(ResourceType.Content, space, "/people", "p2"), selfPatch, "dmart");
            sameVariants.IsOk.ShouldBeTrue($"self-variants update should pass: {sameVariants.ErrorMessage}");

            // Update p2 to take one of p1's skus → must collide.
            var stealSku = new System.Collections.Generic.Dictionary<string, object>
            {
                ["payload"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        variants = new object[] { new { sku = "B" }, new { sku = "Z" } },
                    })).RootElement.Clone(),
                },
            };
            var collide = await entryService.UpdateAsync(
                new Locator(ResourceType.Content, space, "/people", "p2"), stealSku, "dmart");
            collide.IsOk.ShouldBeFalse("variant overlap on update must be rejected");
            collide.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    // A path that doesn't exist on the entry contributes no token. The
    // compound is still probed using the remaining paths (Python parity).
    [FactIfPg]
    public async Task MultiPath_With_Missing_Field_Probes_Remaining_Paths()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries,
            """[["payload.body.email", "payload.body.never_set"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "alice", new { email = "a@x.com" }), "dmart"))
                .IsOk.ShouldBeTrue();

            // never_set is missing on both rows; email collides → reject.
            var dup = await entryService.CreateAsync(
                MakeContent(space, "/people", "bob", new { email = "a@x.com" }), "dmart");
            dup.IsOk.ShouldBeFalse("missing optional path must not mask a collision on the present path");
            dup.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);

            // Distinct email → no collision regardless of missing path.
            var ok = await entryService.CreateAsync(
                MakeContent(space, "/people", "carol", new { email = "c@x.com" }), "dmart");
            ok.IsOk.ShouldBeTrue($"distinct email must pass: {ok.ErrorMessage}");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }
}
