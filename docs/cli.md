# dmart cli — feature tour

`dmart cli` is the interactive client baked into the same `dmart` binary that
runs the server. No separate install, no runtime, no extra config files
beyond an optional `~/.dmart/cli.ini`.

This page is a tour of what's actually in the box — every feature listed is
already implemented in `Cli/`. For the engineering layout see
[architecture.md](./architecture.md); for endpoint shapes see
[data-model.md](./data-model.md).

## Three modes from one binary

| Mode | Invocation | Use when |
|---|---|---|
| REPL | `dmart cli` | Exploring a space interactively |
| Command | `dmart cli c <space> [<subpath>] <cmd…>` | One-shot ops, shell pipelines |
| Script | `dmart cli s <file>` | Repeatable provisioning, CI |

All three share the same command set, the same auth, and the same output
formatting controls — pick the mode that fits the task.

## Connection

```
$ dmart cli
DMART command line interface v0.8.23-0-g876ebcd
  server http://localhost:8282 user dmart
  connected in 115 ms as dmart
Available spaces: applications  archive  management  personal  zainmart
management:/ >
```

The banner surfaces:
- the **CLI build** (the `git describe --long` string baked at compile time, so
  you always know which binary you're holding),
- the **server URL** and **authenticated user**,
- **login latency in ms** — a quick health/sanity check against the server.

Configuration is layered: `./cli.ini` → `~/.dmart/cli.ini` → environment
variables (`DMART_URL`, `DMART_SHORTNAME`, `DMART_PASSWORD`). Env vars override
ini; an interactive warning prints when `DMART_PASSWORD` is set, since it leaks
into `ps` and `/proc/<pid>/environ`.

The HTTP client uses a 30 s timeout (the .NET default of 100 s is too long for
a REPL). Mid-session JWT expiry is handled transparently: any 401 response
triggers a silent re-login + retry, so a long REPL session doesn't drift into
"every command silently fails" territory.

## Navigation

A familiar shell-like model maps onto dmart spaces and subpaths:

```
management:/ > ls
management:/
╭────┬────────────┬─────────────────────────────┬─────────────────────────╮
│  # │ type       │ shortname                   │ payload                 │
├────┼────────────┼─────────────────────────────┼─────────────────────────┤
│  1 │ 📁 folder  │ users                       │                         │
│  2 │ 📁 folder  │ groups                      │                         │
│  3 │ 📄 schema  │ workflow                    │ json / meta_schema      │
│ …                                                                       │
╰────┴────────────┴─────────────────────────────┴─────────────────────────╯
management:/ > cd users
Switched subpath to: /users
management:/users > switch zainmart
Switching space to zainmart
zainmart:/ >
```

- `ls`, `cd`, `pwd`, `switch` (with prefix matching on space names)
- `cd ..` walks up; `cd @space/sub` jumps across spaces in one command
- The prompt **always** shows the current space:subpath
- `whoami` answers "what user / server / space / subpath am I on" without
  having to mentally combine the prompt and the banner
- Tab completion is **context-aware**: it completes commands as the first
  word, space names after `switch`, folders after `cd`, and entry shortnames
  for `cat`/`print`/`rm`. Persisted history (1000 lines) lives at
  `~/.dmart/cli_history`.

## Search

```
zainmart:/ > find brand --type folder --limit 5
╭──────────────┬────────┬─────────────╮
│ subpath      │ type   │ shortname   │
├──────────────┼────────┼─────────────┤
│ /            │ folder │ brands      │
│ /brands      │ folder │ acme_brand  │
│ /brands      │ folder │ globex      │
│ …                                   │
╰──────────────┴────────┴─────────────╯
```

`find <pattern>` wraps the server's `/managed/query` `type=search` endpoint;
`--type` and `--limit` are pass-throughs. Results render in a table by
default, or as raw JSON under `--json`.

## CRUD without ceremony

```
management:/users > mkdir kefah
management:/users > create user alice
management:/users > attach pic alice image profile.png
management:/users > rm --dry-run alice
Would delete: alice (user)
management:/users > rm alice
Delete user 'alice' (may contain children)? [y/N] y
```

- `mkdir` / `create` / `rm` / `move` (alias `mv`) / `attach`
- `print` (metadata) and `cat` (full record); `cat <name>` does prefix match,
  `cat path/name` navigates temporarily, `cat *` dumps the whole listing
- `rm` defaults to **safe**: non-empty folders prompt for confirmation, and
  non-interactive runs (script mode, redirected stdin, `--json`) refuse
  destructive ops without `-f`. `--dry-run` (or `-n`) prints what would be
  deleted without touching anything.
- The whole CRUD set is also reachable via `cli c <space> <cmd…>` for shell
  scripts: `dmart cli c management mkdir kefah`.

### Attachments — single, multilingual, batch

```
# Single attach with multilingual displayname/description
attach myfile alice media profile.png \
  --name-en "Profile picture" --name-ar "صورة الملف الشخصي" \
  --desc-en "Headshot taken at on-boarding"

# Batch — glob expands inside the CLI; one shortname is auto-derived per file
attach --batch alice media './pics/*.png'
```

- `--name-en / --name-ar / --name-ku` populate `displayname`; `--desc-en /
  --desc-ar / --desc-ku` populate `description`. Server stores them in the
  attachment's `displayname` / `description` JSONB columns (visible in the
  catalog UI).
- Quoted strings (single or double) survive the parser, so values with spaces
  and Unicode work fine.
- `--batch <entry> <type> <glob>` shows a real Spectre progress bar with
  per-file ETA when interactive; falls through to silent iteration when
  output is redirected. Per-file shortname is derived from the filename
  (lower-cased, non-`[a-z0-9_]` swapped for underscores). Per-locale flags
  apply to every file in the batch; when no `--name-en` is given, the
  filename becomes the en label so the catalog UI has something to render.

## Export & import

```
# ZIP export from a saved query file (the original form)
export query.json

# ZIP export from CLI shortcut flags — no file needed
export --space management --subpath /users --type folder --limit 100 --out ./users.zip

# CSV export — same flag surface
export csv --space zainmart --subpath /products --type content --limit 1000000 --out ./products.csv
export csv --all --space zainmart --out ./everything.csv

# Round-trip a folder + its contents in one zip via --include-self
export --space management --subpath /rt_test --include-self --out ./rt_test.zip

# ZIP import (a previous export, or any zip following dmart's layout)
import ./rt_test.zip
```

- Both `export` and `export csv` accept either a `<query.json>` path **or**
  the synthesized form via `--space / --subpath / --type / --limit / --from
  / --to / --search / --all / --out`. `--all` mirrors the catalog modal's
  "download everything" toggle: limit becomes 1M and date filters are
  cleared.
- `--include-self` (ZIP only) runs **two passes** under the hood — one for
  `--subpath` itself (the parent folder meta) and one for everything inside
  it — and merges the results into a single zip. Without this flag,
  `--subpath foo` exports entries *under* foo but not foo itself, and
  `import` would silently skip the folder. End-to-end round-trip
  (export → delete → import) preserves entries and attachments
  byte-for-byte (including multilingual displayname/description and media
  bytes).
- Default output path when `--out` is omitted: `~/Downloads/<space>.zip` (or
  `.csv`).
- `import` posts the zip to `/managed/import` — same endpoint cxb's import
  modal uses; identical server-side semantics.

## Output modes

Three orthogonal flags shape output:

| Flag | Effect |
|---|---|
| (none) | Colorized tables, spinners, banner, prompts |
| `--no-color` | Drops ANSI colors but keeps tables/banner. Auto-on when stdout is redirected. |
| `--json` | Drops everything decorative; emits only structured data. Implies `--no-color`. |

```bash
# Pipe-ready JSON for jq
dmart cli --json c management whoami | jq .

# Table for the eye, plain ASCII for `script(1)` capture
dmart cli --no-color c management ls
```

`whoami` and `version` emit one-line JSON objects under `--json` (hand-built,
AOT-safe). `find`, `cat`, and any `print` always emit the server's JSON
response under `--json`. `ls` under `--json` emits the raw entries array.

## Help that knows what you typed

```
management:/ > help
   ╭─ DMart CLI Help ─╮
   │ Command          │ Description                          │
   │ ls [path] [page] │ List entries under current subpath.  │
   │ cd <folder|..|…> │ Enter folder; .. goes up; @space …  │
   │ find <pattern>…  │ Search current space for pattern.    │
   │ rm […] <name|*>  │ Delete entry; --dry-run prints; -f … │
   │ …                                                       │
   ╰──────────────────┴──────────────────────────────────────╯
management:/ > help rm
rm: Delete entry; --dry-run prints; -f skips confirm.
  usage: rm [--dry-run] [-f] <name|*>
management:/ > mkdri kefah
Unknown command: mkdri
  did you mean mkdir?
```

- `help` shows the full index; `help <cmd>` zooms in on one command's usage
  and summary
- Unknown commands trigger a Levenshtein-≤2 suggestion ("did you mean
  mkdir?") instead of leaving the user to retype `help` and scroll

## Scripting

```
# provision-tenant.dmart
VAR space     acme
VAR contact   ${space}_contact

create space ${space}
switch ${space}
mkdir ${contact}
mkdir users
upload schema product products/schema/product.json
```

- `VAR <name> <value>` declares; `${name}` references. The earlier
  bare-name `String.Replace` substitution was a footgun (a `VAR id …` would
  clobber any word containing "id"); the regex form only matches `${…}`.
- `#`, `//` line comments and `/* … */` block comments are stripped.
- `--strict` aborts the script on the first non-success response — pair with
  `set -e` in the surrounding shell for a CI-grade pipeline:
  ```bash
  dmart cli --strict s provision-tenant.dmart || exit 1
  ```

## Resilience

The CLI is meant to stay up across disruptions that would force most
hand-rolled clients to reconnect:

- **30 s HTTP timeout** — a hung server surfaces in seconds, not 100.
- **Transparent token refresh** — a 401 mid-session re-runs `LoginAsync` and
  retries the call once, so JWT expiry doesn't silently break every
  subsequent command.
- **Markup-safe rendering** — every interpolated user value (space, subpath,
  error message, command argument) goes through `Markup.Escape`. A path or
  error message containing `[` won't crash the line.
- **Strict-failure mode for scripts** — pairs with `--strict` to give CI a
  clean exit code on the first error.

## Aesthetics

The output uses a coherent palette defined once in `Cli/CliTheme.cs`:

| Role | Color |
|---|---|
| Path (subpath, URL) | aqua |
| Heading (labels, current space) | yellow |
| Success (entries, ok) | green |
| Warning (recoverable) | yellow |
| Error (failures) | red |
| Muted (latency, hints) | grey |
| Cmd (command names in help) | blue |

Spinner widgets surround `find`, `version`, and `upload` so slow operations
don't feel hung. JSON output is hand-rolled with ANSI colors for `cat` /
`print` / response bodies (the manual writer is AOT-safe and avoids
Spectre's reflection-based formatter dependencies).

## Version awareness

Both `dmart -v` and `dmart cli`'s banner now always carry the **full**
`<tag>-<n>-g<sha>` form (e.g. `v0.8.23-0-g876ebcd`), even on builds cut from
an exact tag. `version` inside the REPL pulls the same field plus the
server's `/info/manifest` so you can verify CLI ↔ server compatibility at a
glance.

```
management:/ > version
  CLI build      │ v0.8.23-0-g876ebcd
  server version │ v0.8.23
  server branch  │ master
```

## What lives where

| File | Role |
|---|---|
| `Cli/CliRunner.cs` | REPL / cmd / script dispatch; banner; global-flag parsing |
| `Cli/CommandHandler.cs` | One handler per command; help registry; did-you-mean; rendering |
| `Cli/DmartClient.cs` | HTTP layer; login + token refresh; AOT-safe JSON request bodies |
| `Cli/DmartCompleter.cs` | ReadLine tab-completion (commands, spaces, folders, entries) |
| `Cli/CliTheme.cs` | Palette + safe markup helpers |
| `Cli/Settings.cs` | `cli.ini` + env-var loader |
