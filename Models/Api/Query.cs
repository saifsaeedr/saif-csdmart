using System.Text.Json;
using Dmart.Models.Enums;

namespace Dmart.Models.Api;

// Mirrors dmart/backend/models/api.py::Query field-for-field. Defaults match dmart's
// Pydantic defaults so omitted fields produce the same wire/behaviour.
public sealed record Query
{
    public required QueryType Type { get; init; }
    public required string SpaceName { get; init; }

    // dmart's DB stores subpaths with a leading slash. Wire callers may send either
    // form (stripped, like "api", or with slash, like "/api"); we normalize so SQL
    // WHERE clauses always query the canonical leading-slash form.
    private readonly string _subpath = "/";
    public required string Subpath
    {
        get => _subpath;
        init => _subpath = Dmart.Models.Core.Locator.NormalizeSubpath(value);
    }
    public bool ExactSubpath { get; init; }
    public List<ResourceType>? FilterTypes { get; init; }
    public List<string> FilterSchemaNames { get; init; } = new() { "meta" };
    public List<string>? FilterShortnames { get; init; } = new();
    public List<string>? FilterTags { get; init; }
    public string? Search { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public List<string>? ExcludeFields { get; init; }
    public List<string>? IncludeFields { get; init; }
    public Dictionary<string, string> HighlightFields { get; init; } = new();
    public string? SortBy { get; init; }
    public SortType? SortType { get; init; }
    public bool RetrieveJsonPayload { get; init; }
    public bool RetrieveAttachments { get; init; }
    // Nullable on purpose: System.Text.Json source-gen does NOT apply C# property
    // initializers when a key is missing from the incoming JSON, so `= true`
    // would flip to false on any request that omits the field. Python defaults
    // retrieve_total to true, so missing → null → interpreted as true by
    // QueryService. Only an explicit `retrieve_total: false` skips the count.
    public bool? RetrieveTotal { get; init; }
    public bool ValidateSchema { get; init; } = true;
    public bool RetrieveLockStatus { get; init; }
    public string? JqFilter { get; init; }
    public int Limit { get; init; } = 10;
    public int Offset { get; init; }
    public RedisAggregate? AggregationData { get; init; }
    public List<JoinQuery>? Join { get; init; }
}

// Mirrors dmart's models/api.py::JoinQuery exactly.
public sealed record JoinQuery
{
    public required string JoinOn { get; init; }
    public required string Alias { get; init; }
    public JsonElement? Query { get; init; }
}

// Mirrors dmart's models/api.py::RedisAggregate.
public sealed record RedisAggregate
{
    public List<string> GroupBy { get; init; } = new();
    public List<RedisReducer> Reducers { get; init; } = new();
    public List<string> Load { get; init; } = new();
}

// Mirrors dmart's models/api.py::RedisReducer.
public sealed record RedisReducer
{
    public required string ReducerName { get; init; }
    public string? Alias { get; init; }
    public List<string> Args { get; init; } = new();
}
