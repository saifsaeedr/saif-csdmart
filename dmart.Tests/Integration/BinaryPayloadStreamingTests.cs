using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Regression guard for the JsonStripEmptiesMiddleware streaming rework: a
// binary (non-JSON) attachment download must pass through the strip middleware
// byte-for-byte, never buffered or mangled. Uploads a multi-hundred-KB media
// payload and byte-compares the download. If the middleware ever buffered or
// re-encoded non-JSON responses again, the SHA/length check here would fail.
public sealed class BinaryPayloadStreamingTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public BinaryPayloadStreamingTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Binary_Payload_Downloads_Byte_For_Byte()
    {
        var space = $"itest_binstream_{Guid.NewGuid():N}"[..20];
        var user = await _factory.CreateLoggedInUserAsync();
        var client = user.Client;

        // Deterministic ~512KB binary blob (no Math.Random — varies by index).
        var blob = new byte[512 * 1024];
        for (var i = 0; i < blob.Length; i++)
            blob[i] = (byte)((i * 31 + 7) & 0xFF);

        try
        {
            (await CreateAsync(client, space,
                $"{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{space}\",\"attributes\":{{\"is_active\":true}}}}"))
                .Status.ShouldBe(Status.Success, "create space");

            (await CreateAsync(client, space,
                "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"bin\",\"attributes\":{\"is_active\":true}}"))
                .Status.ShouldBe(Status.Success, "create folder");

            (await UploadAsync(client, space,
                "{\"resource_type\":\"media\",\"subpath\":\"bin\",\"shortname\":\"blob\",\"attributes\":{\"is_active\":true}}",
                blob, "blob.bin", "application/octet-stream"))
                .Status.ShouldBe(Status.Success, "upload media");

            var resp = await client.GetAsync($"/managed/payload/media/{space}/bin/blob.bin");
            resp.IsSuccessStatusCode.ShouldBeTrue($"download failed: {resp.StatusCode}");
            var downloaded = await resp.Content.ReadAsByteArrayAsync();

            downloaded.Length.ShouldBe(blob.Length, "byte count changed — response was buffered/re-encoded");
            downloaded.ShouldBe(blob, "binary payload was altered in transit");
        }
        finally
        {
            try
            {
                await client.PostAsync("/managed/request",
                    JsonContent(
                        $"{{\"space_name\":\"{space}\",\"request_type\":\"delete\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{space}\",\"attributes\":{{}}}}]}}"));
            }
            catch { /* best effort */ }
            await user.Cleanup();
        }
    }

    private static StringContent JsonContent(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task<Response> CreateAsync(HttpClient client, string space, string record)
    {
        var body = $"{{\"space_name\":\"{space}\",\"request_type\":\"create\",\"records\":[{record}]}}";
        var resp = await client.PostAsync("/managed/request", JsonContent(body));
        return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
    }

    private static async Task<Response> UploadAsync(
        HttpClient client, string space, string record, byte[] payloadBytes,
        string payloadFileName, string payloadMime)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(space), "space_name");

        var recordPart = new ByteArrayContent(Encoding.UTF8.GetBytes(record));
        recordPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(recordPart, "request_record", "request_record.json");

        var payloadPart = new ByteArrayContent(payloadBytes);
        payloadPart.Headers.ContentType = new MediaTypeHeaderValue(payloadMime);
        form.Add(payloadPart, "payload_file", payloadFileName);

        var resp = await client.PostAsync("/managed/resource_with_payload", form);
        return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
    }
}
