# Dmart.QueryGrammar

Shared search-expression parser used by both the dmart server (the AOT-compiled CMS in [edraj/csdmart](https://github.com/edraj/csdmart)) and the `Dmart.SqlAdapter` SDK. Pure SQL-string building over Npgsql — no JSON, no reflection, AOT-clean.

You probably don't depend on this package directly. It's pulled in transitively by `Dmart.SqlAdapter` (and consumed in-process by the dmart server). The standalone package exists so the two consumers can never drift out of sync at the grammar level.

## What it parses

RediSearch-flavoured `@field:value` syntax, originally borrowed from the Python dmart's adapter:

| Form                                | Meaning                                             |
| ----------------------------------- | --------------------------------------------------- |
| `@field:value`                      | exact match (string, numeric, boolean, JSON)        |
| `-@field:value`                     | negation                                            |
| `@field:v1\|v2\|v3`                 | alternation                                         |
| `@field:[lo hi]`                    | range (numeric or lexicographic)                    |
| `@field:>100` / `<` / `>=` / `<=`   | comparison operators                                |
| `@field:!value`                     | not-equal                                           |
| `@field:foo*` / `*foo` / `*foo*`    | wildcard (prefix / suffix / contains)               |
| `@field:null`                       | match field-missing OR JSON null                    |
| `@field:*`                          | existence check (IS NOT NULL)                       |
| `(group1) (group2)`                 | parenthesised groups, AND within, OR between        |
| `@payload.body.x.y:value`           | JSONB path lookup                                   |
| `@payload.body.items[].price:>100`  | array iteration with predicate                      |
| free `word`                         | plain ILIKE across shortname/payload/displayname/…  |

## Usage

```csharp
using Dmart.QueryGrammar;

var parsed = SearchExpressionParser.Parse(
    expression: "@status:open @priority:>5 -@archived:*",
    startingParamIndex: 0,
    style: PlaceholderStyle.Positional,   // $N for server-style commands
    targetTable: "entries");              // skip user-meta join on `users`

foreach (var fragment in parsed.Clauses)
    sql.Append(" AND ").Append(fragment);
foreach (var p in parsed.Parameters)
    cmd.Parameters.Add(p);
```

### Placeholder styles

`PlaceholderStyle.Named` (default) emits `@s_N` parameters — matches the SDK's coexistence with `@space`/`@subpath`/etc.

`PlaceholderStyle.Positional` emits `$N` — for callers (like the dmart server's `QueryHelper`) that already use positional binding. Npgsql is 1-based, so `startingParamIndex = args.Count` produces `$(args.Count + 1)` for the first emitted param.

### `targetTable`

Optional. The grammar's `@email` / `@msisdn` shortcuts join through `owner_shortname IN (SELECT shortname FROM users WHERE ...)` — correct against `entries` / `attachments`, broken against `users` itself (no `owner_shortname` column there). Pass `targetTable: "users"` to skip the join; pass any other value (or `null`) for the default join behaviour.

## Safety helpers

```csharp
SearchExpressionParser.IsSafeForAlternationValue(s)
```

Returns `true` only if `s` contains no character the grammar treats as a metachar. Use this before splicing a user-controlled value into a synthesised `@field:v1|v2|v3` clause.

## License

MIT. See [LICENSE](https://github.com/edraj/csdmart/blob/master/LICENSE).
