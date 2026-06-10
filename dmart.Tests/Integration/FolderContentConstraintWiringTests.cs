using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end proof that FolderContentValidator is actually invoked on the
// /managed/request create, update and move paths plus the multipart upload
// path (the direct-validator logic is covered by FolderContentValidatorTests).
// Uses a super_admin token so the permission layer passes and the request
// reaches the content gate.
//
// Enforcement is OPT-IN (EnforceFolderContentPolicy, default false = dry-run
// warn-only), so every rejection test runs against an enforcing host; the
// dry-run test pins the default-off behaviour.
public class FolderContentConstraintWiringTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public FolderContentConstraintWiringTests(DmartFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> EnforcingHost() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.EnforceFolderContentPolicy = true)));

    private async Task<string> SeedSpaceWithFolderAsync(string folderShortname, string bodyJson)
    {
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var spaceName = $"fcw_{Guid.NewGuid():N}"[..16];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = _factory.AdminShortname, IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await AddFolderAsync(spaceName, folderShortname, bodyJson);
        return spaceName;
    }

    private async Task AddFolderAsync(string spaceName, string folderShortname, string bodyJson)
    {
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = folderShortname, SpaceName = spaceName, Subpath = "/",
            ResourceType = ResourceType.Folder, IsActive = true, OwnerShortname = _factory.AdminShortname,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Payload = new Payload { ContentType = ContentType.Json, Body = JsonDocument.Parse(bodyJson).RootElement.Clone() },
        });
    }

    private async Task CleanupSpaceAsync(string spaceName)
    {
        try { await _factory.Services.GetRequiredService<SpaceRepository>().DeleteAsync(spaceName); } catch { }
    }

    private static async Task<(Response? Response, string RawBody)> PostAsync(HttpClient client, string body)
    {
        var resp = await client.PostAsync("/managed/request", new StringContent(body, Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        return (JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response), raw);
    }

    // /managed/request aggregates per-record failures under
    // error.info[0]["failed"][0]["error_code"] and always emits SOMETHING_WRONG
    // at the top level (Python parity: router.py aggregate failure envelope).
    // This helper drills into the first failed record's error_code so tests can
    // assert the specific per-record code that EntryService / RequestHandler returned.
    private static int? FirstFailedRecordErrorCode(Response result)
    {
        if (result.Error?.Info is not { Count: > 0 } info) return null;
        if (!info[0].TryGetValue("failed", out var failedRaw)) return null;
        if (failedRaw is not JsonElement failedEl || failedEl.ValueKind != JsonValueKind.Array) return null;
        if (failedEl.GetArrayLength() == 0) return null;
        var first = failedEl[0];
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("error_code", out var codeEl)) return null;
        return codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32() : null;
    }

    [FactIfPg]
    public async Task EntryPath_Create_DisallowedResourceType_Rejected()
    {
        var enforcing = EnforcingHost();
        var user = await _factory.CreateLoggedInUserAsync(host: enforcing);
        // Folder accepts only subfolders — a content entry must be rejected.
        var space = await SeedSpaceWithFolderAsync("subdirs", """{"content_resource_types":["folder"]}""");
        try
        {
            var shortname = "c_" + Guid.NewGuid().ToString("N")[..8];
            var body = "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/subdirs\",\"attributes\":{}}]}";
            var (result, _) = await PostAsync(user.Client, body);
            // /managed/request returns SOMETHING_WRONG at the top level when any
            // record fails; the per-record code from EntryService is INVALID_DATA.
            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.SOMETHING_WRONG);
            FirstFailedRecordErrorCode(result).ShouldBe(InternalErrorCode.INVALID_DATA);
        }
        finally
        {
            await CleanupSpaceAsync(space);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task EntryPath_Update_FlipWorkflowToDisallowed_Rejected()
    {
        var enforcing = EnforcingHost();
        var user = await _factory.CreateLoggedInUserAsync(host: enforcing);
        var space = await SeedSpaceWithFolderAsync("tickets", """{"workflow_shortnames":["approval"]}""");
        try
        {
            var shortname = "t_" + Guid.NewGuid().ToString("N")[..8];

            // Create a ticket with an allowed workflow — should succeed. (No real
            // workflow definition exists, so initial-state stays null; that's fine.)
            var createBody = "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"ticket\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/tickets\"," +
                "\"attributes\":{\"workflow_shortname\":\"approval\"}}]}";
            var (createResult, createRaw) = await PostAsync(user.Client, createBody);
            createResult!.Status.ShouldBe(Status.Success, $"ticket create setup failed: {createRaw}");

            // Patch the workflow to a disallowed value — must be rejected.
            var updateBody = "{\"space_name\":\"" + space + "\",\"request_type\":\"update\",\"records\":[" +
                "{\"resource_type\":\"ticket\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/tickets\"," +
                "\"attributes\":{\"workflow_shortname\":\"other\"}}]}";
            var (result, _) = await PostAsync(user.Client, updateBody);
            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.SOMETHING_WRONG);
            FirstFailedRecordErrorCode(result).ShouldBe(InternalErrorCode.INVALID_DATA);
        }
        finally
        {
            await CleanupSpaceAsync(space);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task DedicatedPath_Create_AttachmentOnFolder_DisallowedType_Rejected()
    {
        var enforcing = EnforcingHost();
        var user = await _factory.CreateLoggedInUserAsync(host: enforcing);
        // Folder accepts only content — a comment attached directly to the folder
        // must be rejected (the comment's parent resolves to this Folder).
        var space = await SeedSpaceWithFolderAsync("notes", """{"content_resource_types":["content"]}""");
        try
        {
            var shortname = "cm_" + Guid.NewGuid().ToString("N")[..8];
            var body = "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"comment\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/notes\"," +
                "\"attributes\":{\"body\":\"hi\"}}]}";
            var (result, _) = await PostAsync(user.Client, body);
            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.SOMETHING_WRONG);
            FirstFailedRecordErrorCode(result).ShouldBe(InternalErrorCode.INVALID_DATA);
        }
        finally
        {
            await CleanupSpaceAsync(space);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task DedicatedPath_Create_User_DisallowedType_Rejected()
    {
        // Exercises the RequestHandler-level gate (DispatchCreateAsync's non-entry
        // switch block) for an identity type — the headline "all resource types"
        // case: a folder restricted to content must reject a user created under it.
        // The gate fires before CreateUserAsync, so no user row is ever written.
        var enforcing = EnforcingHost();
        var user = await _factory.CreateLoggedInUserAsync(host: enforcing);
        var space = await SeedSpaceWithFolderAsync("people", """{"content_resource_types":["content"]}""");
        try
        {
            var shortname = "u_" + Guid.NewGuid().ToString("N")[..8];
            var body = "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"user\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/people\",\"attributes\":{}}]}";
            var (result, _) = await PostAsync(user.Client, body);
            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.SOMETHING_WRONG);
            FirstFailedRecordErrorCode(result).ShouldBe(InternalErrorCode.INVALID_DATA);
        }
        finally
        {
            await CleanupSpaceAsync(space);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task MultipartUpload_Attachment_DisallowedType_Rejected()
    {
        // The multipart /managed/resource_with_payload upload path must honor the
        // folder content gate too — without it, content_resource_types could be
        // bypassed by uploading a media attachment instead of POSTing JSON.
        // This endpoint returns StoreAttachmentAsync's Response directly (no batch
        // aggregation), so the top-level error code is INVALID_DATA, not SOMETHING_WRONG.
        var enforcing = EnforcingHost();
        var user = await _factory.CreateLoggedInUserAsync(host: enforcing);
        var space = await SeedSpaceWithFolderAsync("uploads", """{"content_resource_types":["content"]}""");
        try
        {
            var shortname = "md_" + Guid.NewGuid().ToString("N")[..8];
            var recordJson = "{\"resource_type\":\"media\",\"shortname\":\"" + shortname +
                "\",\"subpath\":\"/uploads\",\"attributes\":{}}";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(space), "space_name");
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(recordJson)), "request_record", "request_record.json");
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("hello")), "payload_file", "hello.txt");

            var resp = await user.Client.PostAsync("/managed/resource_with_payload", form);
            var result = JsonSerializer.Deserialize(
                await resp.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA);
        }
        finally
        {
            await CleanupSpaceAsync(space);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task EntryPath_Move_IntoRestrictedFolder_Rejected_WhenEnforced()
    {
        // create-then-move must not bypass the destination folder's policy: an
        // entry born in an unconstrained folder gets re-validated against the
        // DESTINATION parent on move (EntryService.MoveAsync).
        var enforcing = EnforcingHost();
        var user = await _factory.CreateLoggedInUserAsync(host: enforcing);
        var space = await SeedSpaceWithFolderAsync("open", """{}""");
        await AddFolderAsync(space, "locked", """{"content_resource_types":["folder"]}""");
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        try
        {
            var shortname = "m_" + Guid.NewGuid().ToString("N")[..8];
            var createBody = "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/open\",\"attributes\":{}}]}";
            var (createResult, createRaw) = await PostAsync(user.Client, createBody);
            createResult!.Status.ShouldBe(Status.Success, $"create-in-open setup failed: {createRaw}");

            var moveBody = "{\"space_name\":\"" + space + "\",\"request_type\":\"move\",\"records\":[" +
                "{\"resource_type\":\"content\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/open\"," +
                "\"attributes\":{\"dest_subpath\":\"/locked\",\"dest_shortname\":\"" + shortname + "\"}}]}";
            var (result, _) = await PostAsync(user.Client, moveBody);

            result!.Status.ShouldBe(Status.Failed);
            result.Error!.Code.ShouldBe(InternalErrorCode.SOMETHING_WRONG);
            FirstFailedRecordErrorCode(result).ShouldBe(InternalErrorCode.INVALID_DATA);
            // The entry must still live in /open — the rejected move must not
            // have torn it down halfway.
            (await entries.GetAsync(space, "/open", shortname, ResourceType.Content)).ShouldNotBeNull();
            (await entries.GetAsync(space, "/locked", shortname, ResourceType.Content)).ShouldBeNull();
        }
        finally
        {
            await CleanupSpaceAsync(space);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task DryRun_Default_DisallowedCreate_IsAllowed()
    {
        // EnforceFolderContentPolicy defaults to FALSE: deployed folders may carry
        // policy arrays from when they were UI-only rendering hints, so the
        // validator runs in dry-run mode — the violation is warn-logged but the
        // write succeeds. This pins the default so enabling enforcement stays an
        // explicit operator decision.
        var user = await _factory.CreateLoggedInUserAsync();
        var space = await SeedSpaceWithFolderAsync("subdirs", """{"content_resource_types":["folder"]}""");
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        try
        {
            var shortname = "c_" + Guid.NewGuid().ToString("N")[..8];
            var body = "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"shortname\":\"" + shortname + "\",\"subpath\":\"/subdirs\",\"attributes\":{}}]}";
            var (result, raw) = await PostAsync(user.Client, body);

            result!.Status.ShouldBe(Status.Success, $"dry-run default must allow the write: {raw}");
            (await entries.GetAsync(space, "/subdirs", shortname, ResourceType.Content)).ShouldNotBeNull();
        }
        finally
        {
            await CleanupSpaceAsync(space);
            await user.Cleanup();
        }
    }
}
