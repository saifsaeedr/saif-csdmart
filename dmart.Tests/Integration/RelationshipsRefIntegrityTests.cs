using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the referential-integrity gates added in EntryService:
//
//   * write-time (create/update): an entry's relationships[].related_to
//     must resolve to a live entry, else INVALID_DATA (HTTP 400) before
//     the row lands.
//   * delete-time: an entry can't be removed while another entry's
//     relationships still point at it, else CANNT_DELETE (HTTP 400)
//     with the blocker named in the error body.
//
// Scope mirrors what the validators actually check: only entries-table
// types are gated; user/role/permission/space targets pass through
// unchecked. Update validates only NEW relationships (set-diff against
// existing) so a target that gets deleted after persistence does not
// retroactively break unrelated patches. Folder deletes are not RI-gated
// (auditing subtree-external refs is a follow-up).
public class RelationshipsRefIntegrityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public RelationshipsRefIntegrityTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Create_With_Relationship_To_Existing_Entry_Succeeds()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var targetSn = $"reltgt_{Guid.NewGuid():N}".Substring(0, 16);
        var sourceSn = $"relsrc_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            await CreateContent(client, space, subpath, targetSn, relationships: null);

            // Source carries a relationship pointing at the just-created target.
            await CreateContent(client, space, subpath, sourceSn, relationships: new()
            {
                new Dictionary<string, object>
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "content",
                        ["space_name"] = space,
                        ["subpath"] = subpath,
                        ["shortname"] = targetSn,
                    },
                },
            });
        }
        finally
        {
            await DeleteContent(client, space, subpath, sourceSn);
            await DeleteContent(client, space, subpath, targetSn);
        }
    }

    [FactIfPg]
    public async Task Create_With_Relationship_To_Missing_Entry_Fails()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var sourceSn = $"relbad_{Guid.NewGuid():N}".Substring(0, 16);
        var missingSn = $"reldoes_not_exist_{Guid.NewGuid():N}".Substring(0, 16);

        var req = BuildCreate(space, subpath, sourceSn, relationships: new()
        {
            new Dictionary<string, object>
            {
                ["related_to"] = new Dictionary<string, object>
                {
                    ["type"] = "content",
                    ["space_name"] = space,
                    ["subpath"] = subpath,
                    ["shortname"] = missingSn,
                },
            },
        });
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        var body = await resp.Content.ReadAsStringAsync();

        // Mirrors the rest of /managed/request's write-validation handling:
        // FailedResponseFilter maps INVALID_DATA → HTTP 400 and the per-record
        // error lands inside error.info[0].failed[].error. Asserting on both
        // the status code and the message string pins the contract end-to-end.
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldContain("relationship target not found");

        // Confirm the row was NOT persisted: a follow-up GET must 404
        // (validator must reject before the DB upsert runs).
        var getResp = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{sourceSn}");
        getResp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [FactIfPg]
    public async Task Update_That_Adds_Bad_Relationship_Fails()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var sourceSn = $"relupd_{Guid.NewGuid():N}".Substring(0, 16);
        var missingSn = $"relupd_target_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            await CreateContent(client, space, subpath, sourceSn, relationships: null);

            var patch = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = sourceSn,
                        Attributes = new()
                        {
                            ["relationships"] = new List<Dictionary<string, object>>
                            {
                                new()
                                {
                                    ["related_to"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "content",
                                        ["space_name"] = space,
                                        ["subpath"] = subpath,
                                        ["shortname"] = missingSn,
                                    },
                                },
                            },
                        },
                    },
                },
            };
            // The update gate must run the same check as create — same HTTP
            // 400 + structured-body surface as the create test above.
            var resp = await client.PostAsJsonAsync("/managed/request", patch, DmartJsonContext.Default.Request);
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            body.ShouldContain("relationship target not found");
        }
        finally
        {
            await DeleteContent(client, space, subpath, sourceSn);
        }
    }

    // The Materialize/Apply changes also have to land relationships in the
    // entries.relationships JSONB column AND surface them through the GET
    // endpoint as a top-level `relationships` field (Python parity:
    // Meta.model_dump returns relationships as a list, never as a missing
    // key). Without this the RI gate validates data that never appears on
    // the wire — clients can't tell whether an entry has any refs.
    [FactIfPg]
    public async Task Create_Persists_Relationship_So_GET_Returns_It()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var targetSn = $"relrt_{Guid.NewGuid():N}".Substring(0, 16);
        var sourceSn = $"relrs_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            await CreateContent(client, space, subpath, targetSn, relationships: null);
            await CreateContent(client, space, subpath, sourceSn, relationships: new()
            {
                new Dictionary<string, object>
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "content",
                        ["space_name"] = space,
                        ["subpath"] = subpath,
                        ["shortname"] = targetSn,
                    },
                },
            });

            var get = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{sourceSn}");
            var body = await get.Content.ReadAsStringAsync();
            get.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Pin the exact JSON shape: the response is a JSON object with
            // a `relationships` array containing one entry whose related_to
            // names the target by space/subpath/shortname/type. This is the
            // contract clients (and Python's pydantic Meta) expect.
            using var doc = JsonDocument.Parse(body);
            var rels = doc.RootElement.GetProperty("relationships");
            rels.ValueKind.ShouldBe(JsonValueKind.Array);
            rels.GetArrayLength().ShouldBe(1);
            var relatedTo = rels[0].GetProperty("related_to");
            relatedTo.GetProperty("type").GetString().ShouldBe("content");
            relatedTo.GetProperty("space_name").GetString().ShouldBe(space);
            relatedTo.GetProperty("subpath").GetString().ShouldBe(subpath);
            relatedTo.GetProperty("shortname").GetString().ShouldBe(targetSn);

            // Same shape via /managed/query (the other surface): each
            // record carries relationships under attributes.
            var queryReq = new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                Search = $"@shortname:{sourceSn}",
                RetrieveJsonPayload = false,
            };
            var qresp = await client.PostAsJsonAsync("/managed/query", queryReq, DmartJsonContext.Default.Query);
            var qbody = await qresp.Content.ReadAsStringAsync();
            qresp.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var qdoc = JsonDocument.Parse(qbody);
            var records = qdoc.RootElement.GetProperty("records");
            records.GetArrayLength().ShouldBeGreaterThan(0);
            var qrels = records[0].GetProperty("attributes").GetProperty("relationships");
            qrels.ValueKind.ShouldBe(JsonValueKind.Array);
            qrels.GetArrayLength().ShouldBe(1);
            qrels[0].GetProperty("related_to").GetProperty("shortname").GetString().ShouldBe(targetSn);
        }
        finally
        {
            await DeleteContent(client, space, subpath, sourceSn);
            await DeleteContent(client, space, subpath, targetSn);
        }
    }

    // Python parity for the empty case: an entry with no relationships still
    // gets `"relationships": []` in both the GET and query responses (Meta
    // never returns the field as absent). Clients can branch on length
    // instead of a missing-key check.
    [FactIfPg]
    public async Task Entry_Without_Relationships_Returns_Empty_Array()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var sn = $"relempt_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            await CreateContent(client, space, subpath, sn, relationships: null);
            var get = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{sn}");
            var body = await get.Content.ReadAsStringAsync();
            get.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(body);
            var rels = doc.RootElement.GetProperty("relationships");
            rels.ValueKind.ShouldBe(JsonValueKind.Array);
            rels.GetArrayLength().ShouldBe(0);
        }
        finally
        {
            await DeleteContent(client, space, subpath, sn);
        }
    }

    // Backwards-compatibility guarantee: once a relationship is persisted, an
    // unrelated update (here: bumping displayname) must not retroactively
    // fail just because the existing relationship's target is now gone.
    // Without the diff-only gate in EntryService.UpdateAsync, every patch on
    // an old entry would re-run the validator over the full list and break
    // any historical dangling refs.
    [FactIfPg]
    public async Task Update_Unrelated_Field_Still_Works_With_Stale_Relationship()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var targetSn = $"relst_{Guid.NewGuid():N}".Substring(0, 16);
        var sourceSn = $"relsc_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            await CreateContent(client, space, subpath, targetSn, relationships: null);
            await CreateContent(client, space, subpath, sourceSn, relationships: new()
            {
                new Dictionary<string, object>
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "content",
                        ["space_name"] = space,
                        ["subpath"] = subpath,
                        ["shortname"] = targetSn,
                    },
                },
            });

            // Drop the target out from under the source. We don't gate
            // deletion on incoming refs today, so this must succeed.
            await DeleteContent(client, space, subpath, targetSn);

            // Now patch an unrelated field on the source. Validator should
            // see no NEW relationships and pass through.
            var patch = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = sourceSn,
                        Attributes = new() { ["displayname"] = "updated displayname" },
                    },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", patch, DmartJsonContext.Default.Request);
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.ShouldContain("\"status\":\"success\"");
        }
        finally
        {
            await DeleteContent(client, space, subpath, sourceSn);
        }
    }

    // Reverse-RI on delete: deleting an entry that other entries still point
    // at must fail with a diagnostic naming the blocker, so the caller can
    // either fix the references first or accept that the relationship
    // graph is load-bearing. Without this the create-time gate is half-
    // useful — typos are caught up front but later deletions can quietly
    // recreate the dangling-ref problem we just blocked.
    //
    // Modeled as book → author: a book record carries a relationship to its
    // author. Trying to delete the author while at least one book still
    // points at them must surface HTTP 400 with the blocker named in the
    // error body. Deleting the book first must restore the author's
    // ability to be deleted (the gate is dynamic, not sticky).
    [FactIfPg]
    public async Task Delete_Author_Fails_While_Book_References_Them()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var authorSn = $"author1_{Guid.NewGuid():N}".Substring(0, 16);
        var bookSn = $"book1_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            // 1) Author and book both exist; book points at author.
            await CreateContent(client, space, subpath, authorSn, relationships: null);
            await CreateContent(client, space, subpath, bookSn, relationships: new()
            {
                new Dictionary<string, object>
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "content",
                        ["space_name"] = space,
                        ["subpath"] = subpath,
                        ["shortname"] = authorSn,
                    },
                },
            });

            // 2) Deleting the author must fail — book still references it.
            var deleteReq = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = authorSn,
                    },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", deleteReq, DmartJsonContext.Default.Request);
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            body.ShouldContain("incoming relationships");
            body.ShouldContain(bookSn);

            // 3) Author must still be present — the gate ran BEFORE the DB
            // delete, so the row is untouched (not just rolled back).
            var stillThere = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{authorSn}");
            stillThere.StatusCode.ShouldBe(HttpStatusCode.OK);

            // 4) Drop the book first, then the author goes through cleanly.
            await DeleteContent(client, space, subpath, bookSn);
            var resp2 = await client.PostAsJsonAsync("/managed/request", deleteReq, DmartJsonContext.Default.Request);
            resp2.StatusCode.ShouldBe(HttpStatusCode.OK);
            var gone = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{authorSn}");
            gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            // Belt-and-suspenders in case step 4 didn't run.
            await DeleteContent(client, space, subpath, bookSn);
            await DeleteContent(client, space, subpath, authorSn);
        }
    }

    // Cross-table targets (user/role/permission/space) live outside the
    // entries table and aren't reachable via EntryRepository.GetAsync.
    // Probing them would always 404 and break legitimate create flows that
    // record, e.g., an "owned_by user:X" link from a content entry. The gate
    // skips these by type to keep the contract scoped to entries-table refs.
    [FactIfPg]
    public async Task Create_With_Relationship_To_User_Passes_Through()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var sourceSn = $"relusr_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            // Even with a clearly-nonexistent user, the gate must not block
            // — its scope is entries-only.
            await CreateContent(client, space, subpath, sourceSn, relationships: new()
            {
                new Dictionary<string, object>
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "user",
                        ["space_name"] = "management",
                        ["subpath"] = "/users",
                        ["shortname"] = $"nope_{Guid.NewGuid():N}".Substring(0, 16),
                    },
                },
            });
        }
        finally
        {
            await DeleteContent(client, space, subpath, sourceSn);
        }
    }

    // Self-referencing entry: source.relationships → source itself. The
    // reverse-RI gate must allow deletion — the FindFirstReferencerAsync SQL
    // explicitly excludes the row at the target's own (space, subpath,
    // shortname) coordinates so a self-ref doesn't perpetually block its
    // own removal.
    [FactIfPg]
    public async Task Delete_Self_Referencing_Entry_Succeeds()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var sn = $"relself_{Guid.NewGuid():N}".Substring(0, 16);

        // Create-then-update to self-ref: at create time the target doesn't
        // exist yet, so we have to add the relationship in a second step.
        await CreateContent(client, space, subpath, sn, relationships: null);
        var patch = new Request
        {
            RequestType = RequestType.Update,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = sn,
                    Attributes = new()
                    {
                        ["relationships"] = new List<Dictionary<string, object>>
                        {
                            new()
                            {
                                ["related_to"] = new Dictionary<string, object>
                                {
                                    ["type"] = "content",
                                    ["space_name"] = space,
                                    ["subpath"] = subpath,
                                    ["shortname"] = sn,
                                },
                            },
                        },
                    },
                },
            },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", patch, DmartJsonContext.Default.Request);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Delete must succeed — the self-ref does not block.
        var deleteReq = new Request
        {
            RequestType = RequestType.Delete,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = sn,
                },
            },
        };
        var delResp = await client.PostAsJsonAsync("/managed/request", deleteReq, DmartJsonContext.Default.Request);
        delResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var get = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{sn}");
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Update that swaps the relationship list: the existing entry has a ref
    // to target A; the patch replaces it with a ref to target B. The diff-
    // only validator must see {ref→B} as "new" and validate only B; if A
    // was already deleted, the patch still succeeds. If B doesn't exist,
    // the patch fails — proving the diff isn't a "pass everything through"
    // shortcut.
    [FactIfPg]
    public async Task Update_That_Swaps_Relationships_Validates_Only_New_Ones()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var aSn = $"relswpa_{Guid.NewGuid():N}".Substring(0, 16);
        var bSn = $"relswpb_{Guid.NewGuid():N}".Substring(0, 16);
        var srcSn = $"relswps_{Guid.NewGuid():N}".Substring(0, 16);
        var ghostSn = $"relswpg_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            // Build: A, B, source→A.
            await CreateContent(client, space, subpath, aSn, relationships: null);
            await CreateContent(client, space, subpath, bSn, relationships: null);
            await CreateContent(client, space, subpath, srcSn, relationships: new()
            {
                new Dictionary<string, object>
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "content",
                        ["space_name"] = space,
                        ["subpath"] = subpath,
                        ["shortname"] = aSn,
                    },
                },
            });

            // Delete A out from under the source. The source still points at A,
            // but the next patch will replace the whole list with {→B}.
            await DeleteContent(client, space, subpath, aSn);

            // Swap: replace [→A] with [→B]. B is valid → patch succeeds.
            var swapToValid = SwapRelationshipsRequest(space, subpath, srcSn, bSn);
            var resp = await client.PostAsJsonAsync("/managed/request", swapToValid, DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await resp.Content.ReadAsStringAsync()).ShouldContain("\"status\":\"success\"");

            // Swap again: replace [→B] with [→ghost]. ghost doesn't exist →
            // diff sees ref→ghost as "new", validator must fail.
            var swapToGhost = SwapRelationshipsRequest(space, subpath, srcSn, ghostSn);
            var bad = await client.PostAsJsonAsync("/managed/request", swapToGhost, DmartJsonContext.Default.Request);
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await bad.Content.ReadAsStringAsync()).ShouldContain("relationship target not found");
        }
        finally
        {
            await DeleteContent(client, space, subpath, srcSn);
            await DeleteContent(client, space, subpath, bSn);
            await DeleteContent(client, space, subpath, aSn);
        }
    }

    // Multiple relationships, one missing: the validator returns the FIRST
    // miss in source order — the error message must name the second
    // relationship (the missing one), not the third (which is also valid).
    // Pins the batched validator's source-order semantics.
    [FactIfPg]
    public async Task Create_With_Multiple_Relationships_One_Missing_Fails_On_First_Miss()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var t1Sn = $"relm1_{Guid.NewGuid():N}".Substring(0, 16);
        var t3Sn = $"relm3_{Guid.NewGuid():N}".Substring(0, 16);
        var missingSn = $"relmiss_{Guid.NewGuid():N}".Substring(0, 16);
        var srcSn = $"relmsrc_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            await CreateContent(client, space, subpath, t1Sn, relationships: null);
            await CreateContent(client, space, subpath, t3Sn, relationships: null);

            var req = BuildCreate(space, subpath, srcSn, relationships: new()
            {
                RelTo(space, subpath, t1Sn),
                RelTo(space, subpath, missingSn),  // second position; this is the miss.
                RelTo(space, subpath, t3Sn),
            });
            var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            // Anchor on the missing shortname rather than position — the
            // batched validator returns the first miss in source order, so
            // the error names the second relationship's target.
            body.ShouldContain("relationship target not found");
            body.ShouldContain(missingSn);
            body.ShouldNotContain(t3Sn);  // never reached the third — source order, first miss wins.

            // Source must not have landed.
            var get = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{srcSn}");
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await DeleteContent(client, space, subpath, srcSn);
            await DeleteContent(client, space, subpath, t1Sn);
            await DeleteContent(client, space, subpath, t3Sn);
        }
    }

    // Patch with `"relationships": null` must clear the column. Both the
    // in-process literal-null path and the HTTP JsonElement(Null) path must
    // collapse to the same outcome. GET afterwards shows the empty array
    // (EntryHandler.Convert materializes `[]` for null).
    [FactIfPg]
    public async Task Patch_Relationships_Null_Clears_Column()
    {
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();
        var space = "test";
        var subpath = "/itest";
        var targetSn = $"relclr_t_{Guid.NewGuid():N}".Substring(0, 16);
        var srcSn = $"relclr_s_{Guid.NewGuid():N}".Substring(0, 16);

        try
        {
            await CreateContent(client, space, subpath, targetSn, relationships: null);
            await CreateContent(client, space, subpath, srcSn, relationships: new()
            {
                RelTo(space, subpath, targetSn),
            });

            // Sanity: relationship is there.
            var pre = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{srcSn}");
            using (var preDoc = JsonDocument.Parse(await pre.Content.ReadAsStringAsync()))
                preDoc.RootElement.GetProperty("relationships").GetArrayLength().ShouldBe(1);

            // Patch with explicit JSON null. Source-gen lands this as a
            // JsonElement of ValueKind.Null on the dict value, so the patch
            // handler's JsonElement(Null) branch is the one under test.
            var patchJson = $$"""
                {
                    "request_type": "update",
                    "space_name": "{{space}}",
                    "records": [
                        {
                            "resource_type": "content",
                            "subpath": "{{subpath}}",
                            "shortname": "{{srcSn}}",
                            "attributes": { "relationships": null }
                        }
                    ]
                }
                """;
            using var httpContent = new StringContent(patchJson, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/managed/request", httpContent);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // GET must now report `relationships: []` — the column was
            // cleared, and EntryHandler.Convert materializes the empty
            // array (Python parity).
            var get = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{srcSn}");
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            var rels = doc.RootElement.GetProperty("relationships");
            rels.ValueKind.ShouldBe(JsonValueKind.Array);
            rels.GetArrayLength().ShouldBe(0);
        }
        finally
        {
            await DeleteContent(client, space, subpath, srcSn);
            await DeleteContent(client, space, subpath, targetSn);
        }
    }

    // -- helpers --

    // One-relationship swap patch: replaces the whole relationships list
    // with a single ref→shortname pointer. Used by the swap tests.
    private static Request SwapRelationshipsRequest(string space, string subpath, string sourceSn, string targetSn)
        => new()
        {
            RequestType = RequestType.Update,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = sourceSn,
                    Attributes = new()
                    {
                        ["relationships"] = new List<Dictionary<string, object>>
                        {
                            RelTo(space, subpath, targetSn),
                        },
                    },
                },
            },
        };

    private static Dictionary<string, object> RelTo(string space, string subpath, string shortname)
        => new()
        {
            ["related_to"] = new Dictionary<string, object>
            {
                ["type"] = "content",
                ["space_name"] = space,
                ["subpath"] = subpath,
                ["shortname"] = shortname,
            },
        };

    private static async Task CreateContent(HttpClient client, string space, string subpath,
        string shortname, List<Dictionary<string, object>>? relationships)
    {
        var req = BuildCreate(space, subpath, shortname, relationships);
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new Xunit.Sdk.XunitException($"create failed: {await resp.Content.ReadAsStringAsync()}");
        var body = await resp.Content.ReadAsStringAsync();
        if (!body.Contains("\"status\":\"success\"", StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"create returned non-success body: {body}");
    }

    private static Request BuildCreate(string space, string subpath, string shortname,
        List<Dictionary<string, object>>? relationships)
    {
        var attributes = new Dictionary<string, object>
        {
            ["is_active"] = true,
            ["displayname"] = "rel int probe",
        };
        if (relationships is not null) attributes["relationships"] = relationships;
        return new Request
        {
            RequestType = RequestType.Create,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = attributes,
                },
            },
        };
    }

    private static async Task DeleteContent(HttpClient client, string space, string subpath, string shortname)
    {
        try
        {
            var req = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = shortname,
                    },
                },
            };
            await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        }
        catch { /* best-effort cleanup */ }
    }
}
