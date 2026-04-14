using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dmart.Cli;

// HTTP client for dmart REST API — mirrors Python cli.py's DMart class.
// All JSON request bodies are built as literal strings for AOT compatibility
// (no reflection-based serialization).
public sealed class DmartClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CliSettings _settings;
    private string? _token;

    public List<string> SpaceNames { get; private set; } = new();
    public string CurrentSpace { get; set; }
    public string CurrentSubpath { get; set; } = "/";
    public List<JsonElement> CurrentEntries { get; private set; } = new();

    public DmartClient(CliSettings settings)
    {
        _settings = settings;
        CurrentSpace = settings.DefaultSpace;
        _http = new HttpClient { BaseAddress = new Uri(settings.Url.TrimEnd('/')) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ---- Auth ----

    public async Task<(bool Ok, string? Error)> LoginAsync()
    {
        HttpResponseMessage resp;
        try
        {
            var body = Json($"{{\"shortname\":\"{Esc(_settings.Shortname)}\",\"password\":\"{Esc(_settings.Password)}\"}}");
            resp = await _http.PostAsync("/user/login", body);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Cannot connect to {_settings.Url}: {ex.InnerException?.Message ?? ex.Message}");
        }
        var json = await ParseAsync(resp);
        if (json.TryGetProperty("status", out var st) && st.GetString() == "success")
        {
            _token = json.GetProperty("records")[0].GetProperty("attributes")
                .GetProperty("access_token").GetString();
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return (true, null);
        }
        var msg = json.TryGetProperty("error", out var err) ? err.GetProperty("message").GetString() : "login failed";
        return (false, msg);
    }

    // ---- Spaces ----

    public async Task<List<string>> FetchSpacesAsync(bool force = false)
    {
        if (!force && SpaceNames.Count > 0) return SpaceNames;
        try
        {
            var resp = await PostQueryAsync(
                $"{{\"type\":\"spaces\",\"space_name\":\"management\",\"subpath\":\"/\",\"limit\":100}}");
            if (resp.TryGetProperty("records", out var recs))
                SpaceNames = recs.EnumerateArray().Select(r => r.GetProperty("shortname").GetString()!).ToList();
        }
        catch { /* query failed — use whatever we have */ }
        if (!SpaceNames.Contains(CurrentSpace))
            SpaceNames.Insert(0, CurrentSpace);
        return SpaceNames;
    }

    // ---- Navigation ----

    public async Task ListAsync()
    {
        var sub = CurrentSubpath.Replace("//", "/");
        var resp = await PostQueryAsync(
            $"{{\"space_name\":\"{Esc(CurrentSpace)}\",\"type\":\"subpath\",\"subpath\":\"{Esc(sub)}\",\"retrieve_json_payload\":true,\"limit\":100}}");
        CurrentEntries.Clear();
        if (resp.TryGetProperty("records", out var recs))
            CurrentEntries = recs.EnumerateArray().ToList();
    }

    // ---- CRUD ----

    public Task<JsonElement> CreateFolderAsync(string shortname)
        => ManagedRequestAsync("create", RecordJson("folder", CurrentSubpath, shortname, "\"is_active\":true"));

    public Task<JsonElement> CreateEntryAsync(string shortname, string resourceType)
        => ManagedRequestAsync("create", RecordJson(resourceType, CurrentSubpath, shortname, "\"is_active\":true"));

    public Task<JsonElement> DeleteAsync(string shortname, string resourceType)
        => ManagedRequestAsync("delete", RecordJson(resourceType, CurrentSubpath, shortname, null));

    public async Task<JsonElement> MoveAsync(string resourceType, string srcSubpath, string srcShortname,
        string destSubpath, string destShortname)
    {
        var attrs = $"\"src_subpath\":\"{Esc(srcSubpath)}\",\"src_shortname\":\"{Esc(srcShortname)}\",\"dest_subpath\":\"{Esc(destSubpath)}\",\"dest_shortname\":\"{Esc(destShortname)}\"";
        return await ManagedRequestAsync("move",
            RecordJson(resourceType, CurrentSubpath, srcShortname, attrs));
    }

    public async Task<JsonElement> ManageSpaceAsync(string spaceName, string requestType)
    {
        var json = $"{{\"space_name\":\"{Esc(spaceName)}\",\"request_type\":\"{Esc(requestType)}\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{Esc(spaceName)}\",\"attributes\":{{}}}}]}}";
        var resp = await _http.PostAsync("/managed/request", Json(json));
        var result = await ParseAsync(resp);
        await FetchSpacesAsync(force: true);
        return result;
    }

    public async Task<JsonElement> ProgressTicketAsync(string subpath, string shortname, string action)
    {
        var resp = await _http.PutAsync($"/managed/progress-ticket/{CurrentSpace}/{subpath}/{shortname}/{action}", null);
        return await ParseAsync(resp);
    }

    // ---- Upload ----

    public Task<JsonElement> UploadSchemaAsync(string shortname, string filePath)
    {
        var recordJson = $"{{\"resource_type\":\"schema\",\"subpath\":\"schema\",\"shortname\":\"{Esc(shortname)}\",\"attributes\":{{\"schema_shortname\":\"meta_schema\",\"is_active\":true}}}}";
        return UploadWithPayloadAsync(recordJson, filePath);
    }

    public async Task<JsonElement> UploadCsvAsync(string resourceType, string subpath, string schemaShortname, string filePath)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        form.Add(new StreamContent(fs), "resources_file", Path.GetFileName(filePath));
        var resp = await _http.PostAsync(
            $"/managed/resources_from_csv/{resourceType}/{CurrentSpace}/{subpath}/{schemaShortname}", form);
        return await ParseAsync(resp);
    }

    public Task<JsonElement> AttachAsync(string shortname, string entryShortname, string payloadType, string filePath)
    {
        var sub = $"{CurrentSubpath}/{entryShortname}".Replace("//", "/");
        var recordJson = $"{{\"shortname\":\"{Esc(shortname)}\",\"resource_type\":\"{Esc(payloadType)}\",\"subpath\":\"{Esc(sub)}\",\"attributes\":{{\"is_active\":true}}}}";
        return UploadWithPayloadAsync(recordJson, filePath);
    }

    // ---- Import / Export ----

    public async Task<JsonElement> ImportZipAsync(string filePath)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        form.Add(new StreamContent(fs), "zip_file", Path.GetFileName(filePath));
        var resp = await _http.PostAsync("/managed/import", form);
        return await ParseAsync(resp);
    }

    public async Task<string> ExportAsync(string queryJsonPath)
    {
        var queryJson = await File.ReadAllTextAsync(queryJsonPath);
        var resp = await _http.PostAsync("/managed/export", Json(queryJson));
        if (!resp.IsSuccessStatusCode)
        {
            var err = await ParseAsync(resp);
            return err.ToString();
        }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var outPath = Path.Combine(downloads, "export.zip");
        await using var outFile = File.Create(outPath);
        await resp.Content.CopyToAsync(outFile);
        return $"Exported to {outPath}";
    }

    // ---- Query ----

    public Task<JsonElement> QueryAsync(string queryJson)
        => PostQueryAsync(queryJson);

    // ---- Meta / Payload ----

    public async Task<JsonElement> MetaAsync(string resourceType, string shortname)
    {
        var resp = await _http.GetAsync($"/managed/meta/{resourceType}/{CurrentSpace}/{CurrentSubpath}/{shortname}");
        return await ParseAsync(resp);
    }

    public async Task<JsonElement> PayloadAsync(string resourceType, string shortname)
    {
        var resp = await _http.GetAsync($"/managed/payload/{resourceType}/{CurrentSpace}/{CurrentSubpath}/{shortname}.json");
        return await ParseAsync(resp);
    }

    // ---- Request (raw JSON file) ----

    public async Task<JsonElement> RequestFromFileAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var resp = await _http.PostAsync("/managed/request", Json(json));
        return await ParseAsync(resp);
    }

    // ---- Internals ----

    private async Task<JsonElement> ManagedRequestAsync(string requestType, string recordJson)
    {
        var json = $"{{\"space_name\":\"{Esc(CurrentSpace)}\",\"request_type\":\"{Esc(requestType)}\",\"records\":[{recordJson}]}}";
        var resp = await _http.PostAsync("/managed/request", Json(json));
        return await ParseAsync(resp);
    }

    private async Task<JsonElement> PostQueryAsync(string queryJson)
    {
        var resp = await _http.PostAsync("/managed/query", Json(queryJson));
        return await ParseAsync(resp);
    }

    private async Task<JsonElement> UploadWithPayloadAsync(string recordJson, string filePath)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(recordJson, Encoding.UTF8, "application/json"), "request_record", "record.json");
        form.Add(new StringContent(CurrentSpace), "space_name");
        await using var fs = File.OpenRead(filePath);
        form.Add(new StreamContent(fs), "payload_file", Path.GetFileName(filePath));
        var resp = await _http.PostAsync("/managed/resource_with_payload", form);
        return await ParseAsync(resp);
    }

    // Build a single record JSON object for /managed/request
    private static string RecordJson(string resourceType, string subpath, string shortname, string? attrsInner)
    {
        var attrs = attrsInner is not null ? $"{{{attrsInner}}}" : "{}";
        return $"{{\"resource_type\":\"{Esc(resourceType)}\",\"subpath\":\"{Esc(subpath)}\",\"shortname\":\"{Esc(shortname)}\",\"attributes\":{attrs}}}";
    }

    private static StringContent Json(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ParseAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(text).RootElement; }
        catch { return JsonDocument.Parse($"{{\"raw\":\"{Esc(text)}\"}}").RootElement; }
    }

    // Escape a string for embedding in JSON
    private static string Esc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    public void Dispose() => _http.Dispose();
}
