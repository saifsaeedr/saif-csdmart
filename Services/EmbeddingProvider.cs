using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dmart.Services;

// Coordinates vector embeddings for semantic search.
//
// Two things have to be true for semantic features to be "live":
//   1. pgvector is installed in PostgreSQL AND the `entries.embedding` column
//      exists. We probe both at startup (see IsPgVectorAvailableAsync).
//   2. EmbeddingApiUrl is configured — otherwise we have nothing to call
//      to turn text into a vector.
//
// When either is missing, everything no-ops gracefully. Entries are still
// created/updated normally; the semantic_search endpoint + MCP tool return
// a clean "not configured" error and the rest of the stack is unaffected.
//
// Request shape matches the OpenAI embeddings API, which is the de-facto
// standard — works against OpenAI directly, Ollama's compatible bridge,
// text-embeddings-inference, Anthropic's Voyage gateway, and so on.
public sealed class EmbeddingProvider(
    IHttpClientFactory httpFactory,
    IOptions<DmartSettings> settings,
    Db db,
    ILogger<EmbeddingProvider> log)
{
    // Cached once on first access — avoids a round-trip per embed call.
    private bool? _pgVectorAvailable;
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    // Total embeddable text cap. Most embedding APIs have an 8k-token limit;
    // at ~4 chars/token that's ~32k chars. We clip well below that to keep
    // the request body small and the API bill predictable.
    private const int MaxEmbedChars = 8000;

    // 2s timeout per call — embeddings should be fast; if the provider is
    // slow, we'd rather surface a failure than block a write for 10+ seconds.
    private const int EmbedTimeoutSeconds = 2;

    public bool IsProviderConfigured =>
        !string.IsNullOrWhiteSpace(settings.Value.EmbeddingApiUrl);

    // Combined probe — both conditions must hold. Null = not-yet-probed.
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        if (!IsProviderConfigured) return false;
        return await IsPgVectorAvailableAsync(ct);
    }

    public async Task<bool> IsPgVectorAvailableAsync(CancellationToken ct = default)
    {
        if (_pgVectorAvailable is bool cached) return cached;
        await _probeLock.WaitAsync(ct);
        try
        {
            if (_pgVectorAvailable is bool cachedInner) return cachedInner;
            if (!db.IsConfigured) { _pgVectorAvailable = false; return false; }

            bool available;
            try
            {
                await using var conn = await db.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'entries' AND column_name = 'embedding'
                    )
                    """, conn);
                available = Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "pgvector probe failed — semantic search disabled");
                available = false;
            }
            _pgVectorAvailable = available;
            if (!available)
                log.LogInformation(
                    "pgvector not installed or entries.embedding column missing — semantic search disabled");
            return available;
        }
        finally { _probeLock.Release(); }
    }

    // Produces an embedding for a text blob. Returns null on any failure —
    // callers treat null as "skip this entry" rather than erroring.
    //
    // Two modes:
    //   * `mock://hash` (or any `mock://...` URL) — produces a deterministic
    //     hash-based unit vector locally. Same input → same vector, so
    //     exact-match queries rank the target entry first. Useful for CI,
    //     local development, and verifying the pipeline end-to-end without
    //     a real embedding service. Not "semantic" in any meaningful sense;
    //     it just proves create → embed → store → search works.
    //   * Any other URL — OpenAI-compatible HTTP POST.
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (!IsProviderConfigured) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Length > MaxEmbedChars ? text[..MaxEmbedChars] : text;
        var s = settings.Value;

        if (s.EmbeddingApiUrl.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
            return MockEmbed(trimmed);

        var payload = new Dictionary<string, object>
        {
            ["model"] = s.EmbeddingModel,
            ["input"] = trimmed,
        };
        var body = JsonSerializer.Serialize(payload, DmartJsonContext.Default.DictionaryStringObject);

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(EmbedTimeoutSeconds);
        using var req = new HttpRequestMessage(HttpMethod.Post, s.EmbeddingApiUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(s.EmbeddingApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.EmbeddingApiKey);

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("embedding POST {Url} → {Status}: {Body}",
                    s.EmbeddingApiUrl, (int)resp.StatusCode, Trim(err, 200));
                return null;
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return null;
            if (!data[0].TryGetProperty("embedding", out var emb) ||
                emb.ValueKind != JsonValueKind.Array)
                return null;
            var vec = new float[emb.GetArrayLength()];
            var i = 0;
            foreach (var el in emb.EnumerateArray())
                vec[i++] = (float)el.GetDouble();
            return vec;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "embedding call threw — treating as no-op");
            return null;
        }
    }

    // Writes a vector to entries.embedding for the given uuid. Uses the
    // pgvector literal format `'[1.23,4.56,...]'::vector` because Npgsql
    // doesn't have a native parameter type for vectors unless you install
    // the Npgsql.EntityFrameworkCore.PostgreSQL.Vector plugin — which we
    // skip to keep the dependency graph flat.
    public async Task UpdateEntryEmbeddingAsync(
        string entryUuid, float[] vector, CancellationToken ct = default)
    {
        if (vector.Length == 0) return;
        var literal = FormatVectorLiteral(vector);
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE entries SET embedding = $1::vector WHERE uuid = $2", conn);
        cmd.Parameters.Add(new() { Value = literal });
        cmd.Parameters.Add(new() { Value = Guid.Parse(entryUuid) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Deterministic hash-based "embedder" for tests + local dev. Each text
    // maps to a fixed 128-dim unit vector via SHA-256-seeded PRNG. Same
    // input → same vector → cosine distance 0 → rank 1 in queries. Same
    // tokens with different positions also collide (low quality for real
    // semantics but fine for round-trip verification).
    internal static float[] MockEmbed(string text)
    {
        const int Dim = 128;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var seedBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        // Mulberry32-style PRNG seeded off the first 4 bytes of SHA-256.
        uint state = BitConverter.ToUInt32(seedBytes, 0);
        var v = new float[Dim];
        double norm = 0.0;
        for (var i = 0; i < Dim; i++)
        {
            state += 0x6D2B79F5;
            var t = state;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + ((t ^ (t >> 7)) * (t | 61));
            var f = ((t ^ (t >> 14)) & 0xFFFFFF) / (float)0xFFFFFF * 2.0f - 1.0f;
            v[i] = f;
            norm += f * f;
        }
        // Normalize to unit length so cosine similarity is bounded in [-1, 1].
        var scale = norm > 0 ? 1.0 / Math.Sqrt(norm) : 1.0;
        for (var i = 0; i < Dim; i++) v[i] = (float)(v[i] * scale);
        return v;
    }

    // `[1.23,4.56,...]` — pgvector's canonical text form.
    internal static string FormatVectorLiteral(float[] vec)
    {
        var sb = new StringBuilder(vec.Length * 8 + 2);
        sb.Append('[');
        for (var i = 0; i < vec.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vec[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
