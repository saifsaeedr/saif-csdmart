# dmart External Plugin Development Guide

Build external plugins that dmart loads at runtime — no dmart recompilation needed.

## Two Plugin Modes

| Mode | Deploy as | Crash safe | Latency | Best for |
|------|-----------|------------|---------|----------|
| **Subprocess** (recommended) | Executable | Yes — auto-respawn | ~0.1ms | Any language (Python, Node, Go, Rust, C#, bash) |
| **In-process .so** | Shared library | No — crashes dmart | ~1us | Performance-critical C/Rust/C# plugins |

dmart tries **executable first**, falls back to `.so`. If the directory has both, the executable wins.

---

## Subprocess Plugins (Recommended)

The simplest way to write a plugin. Your plugin is a standalone executable that reads JSON lines from stdin and writes JSON lines to stdout. If it crashes, dmart respawns it automatically.

### Protocol

dmart sends one JSON line per message to your plugin's stdin. Your plugin writes one JSON line response to stdout.

**Message types:**

```
→ stdin:  {"type":"info"}
← stdout: {"shortname":"my_plugin","version":"1.0.0","type":"hook"}

→ stdin:  {"type":"hook","event":{...}}
← stdout: {"status":"ok"}

→ stdin:  {"type":"request","request":{...}}
← stdout: {"status":"success","attributes":{...}}
```

Debug output goes to stderr (forwarded to dmart's console).

**`version` is optional but recommended.** Surface your plugin's version so operators can see it on `GET /info/plugins` and in dmart's startup log (`SUBPROCESS_PLUGIN_REGISTERED: my_plugin v1.0.0 (hook) …`). Source the literal from your build artifact rather than hand-maintaining it in code: read it from `package.json`/`__version__`/the value linked by `go build -ldflags "-X main.Version=..."`. Absent versions resolve to `"0.0.0"`.

### Quick Start (Python)

```python
#!/usr/bin/env python3
import json, sys

for line in sys.stdin:
    msg = json.loads(line.strip())

    if msg["type"] == "info":
        print(json.dumps({"shortname": "my_hook", "version": "1.0.0", "type": "hook"}), flush=True)

    elif msg["type"] == "hook":
        event = msg["event"]
        print(f"[my_hook] {event['action_type']} {event['space_name']}/{event.get('shortname','?')}", file=sys.stderr)
        print(json.dumps({"status": "ok"}), flush=True)
```

### Quick Start (Bash)

```bash
#!/bin/bash
while IFS= read -r line; do
    type=$(echo "$line" | jq -r '.type')
    case "$type" in
        info)    echo '{"shortname":"bash_hook","version":"1.0.0","type":"hook"}' ;;
        hook)    echo '{"status":"ok"}' ;;
        request) echo '{"status":"success","attributes":{"hello":"world"}}' ;;
    esac
done
```

### Quick Start (Node.js)

```javascript
#!/usr/bin/env node
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin });

rl.on('line', line => {
    const msg = JSON.parse(line);
    if (msg.type === 'info')
        console.log(JSON.stringify({shortname: 'node_hook', version: '1.0.0', type: 'hook'}));
    else if (msg.type === 'hook')
        console.log(JSON.stringify({status: 'ok'}));
});
```

### Deploy

```bash
mkdir -p ~/.dmart/plugins/my_hook

# Copy your script/binary as the plugin executable (name matches directory)
cp my_hook.py ~/.dmart/plugins/my_hook/my_hook
chmod +x ~/.dmart/plugins/my_hook/my_hook

# Add config — see "Filter shape" below for the full vocabulary.
cat > ~/.dmart/plugins/my_hook/config.json << 'EOF'
{
  "shortname": "my_hook",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "filters": {
    "subpaths": { "__all_spaces__": ["__all_subpaths__"] },
    "resource_types": ["content"],
    "schema_shortnames": [],
    "actions": ["create", "update", "delete"]
  }
}
EOF

# Restart dmart
# Look for: SUBPROCESS_PLUGIN_REGISTERED: my_hook (hook)
```

### Subprocess API Plugin

Same protocol, but respond to `{"type":"request","request":{...}}`:

```python
#!/usr/bin/env python3
import json, sys

for line in sys.stdin:
    msg = json.loads(line.strip())

    if msg["type"] == "info":
        print(json.dumps({
            "shortname": "my_api",
            "version": "1.0.0",
            "type": "api",
            "routes": [
                {"method": "GET", "path": "/"},
                {"method": "GET", "path": "/greet/{name}"}
            ]
        }), flush=True)

    elif msg["type"] == "request":
        req = msg["request"]
        path = req.get("path", "/")
        user = req.get("user", "anonymous")

        if "/greet/" in path:
            name = path.split("/greet/")[1].rstrip("/")
            print(json.dumps({"status": "success", "attributes": {"greeting": f"Hello, {name}!"}}), flush=True)
        else:
            print(json.dumps({"status": "success", "attributes": {"plugin": "my_api", "user": user}}), flush=True)
```

---

## In-Process .so Plugins (Advanced)

For maximum performance. The plugin is a native shared library loaded directly into dmart's process via `NativeLibrary.Load`. **A crash in the plugin crashes dmart.**

### C-ABI Contract

Every `.so` must export these C functions:

| Export | Signature | Required |
|--------|-----------|----------|
| `get_info` | `() → char*` | Yes |
| `hook` | `(char* event_json) → char*` | Hook plugins |
| `handle_request` | `(char* request_json) → char*` | API plugins |
| `free_string` | `(char* ptr) → void` | Yes |
| `init` | `(const DmartCallbacks* cbs) → void` | Optional |
| `dmart_plugin_version` | `() → const char*` | Optional |

All strings are null-terminated UTF-8. The plugin allocates return strings via its own allocator and dmart calls `free_string()` to release them — **except** for `dmart_plugin_version`: that pointer must reference a static literal (typically `.rodata`) owned by the plugin for its lifetime, and dmart does NOT free it.

### Plugin version (`dmart_plugin_version`)

Optional one-liner that returns the plugin's version string, baked into the binary at compile time. dmart resolves the value via `dlsym(handle, "dmart_plugin_version")` once at load time and surfaces it via `GET /info/plugins` and the `NATIVE_PLUGIN_REGISTERED: my_plugin v1.2.3 (hook, in-process) …` startup line. Absent symbol resolves to `"0.0.0"`.

Drive the literal from your build's version constant — `gcc -DPLUGIN_VERSION=\"$(VERSION)\"`, an embedded build-stamp file generated by your release pipeline, etc. — so the version that ships with the binary is the version operators see.

```c
// C — define from a build-time -DPLUGIN_VERSION="1.2.3" macro
const char* dmart_plugin_version(void) { return PLUGIN_VERSION; }
```

```rust
// Rust — env! reads CARGO_PKG_VERSION at compile time
#[no_mangle]
pub extern "C" fn dmart_plugin_version() -> *const std::os::raw::c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const _
}
```

```csharp
// C# — UnmanagedCallersOnly returns a pointer to a UTF-8 byte buffer that
// lives in the assembly's read-only data section.
[UnmanagedCallersOnly(EntryPoint = "dmart_plugin_version")]
public static IntPtr GetVersion() => StaticVersionPtr;
private static readonly IntPtr StaticVersionPtr =
    Marshal.StringToHGlobalAnsi("1.2.3");  // never freed; process-lifetime
```

### Calling back into dmart (`init` + `DmartCallbacks`)

If the plugin exports `init`, dmart calls it once at load time (right after `get_info`) with a pointer to a stable `DmartCallbacks` struct. Store the struct in a static and use it from `hook()` to read/write entries, send email, or push WebSocket messages — no HTTP, no JWT, in-process.

**Canonical struct layout** lives in [`shared/DmartCallbacks.cs`](shared/DmartCallbacks.cs) — that file is the single source of truth and is what the host emits (`Plugins/Native/NativePluginCallbacks.cs:DmartCallbacks`). Past V1-only inline snippets here drifted on every version bump; the pointer is now the contract.

Capability marker bumps so far:

- **V1** — initial release: `load_entry`, `load_user`, `save_entry`, `update_user`, `send_email`, `ws_broadcast`, `dmart_free`
- **V2** — `query`, `get_media_attachment` appended (query was ACL-free)
- **V3** — `query` honors the caller's actor by default; `"as_actor"` override
- **V4** — `log` appended (plugin → ILogger pipeline)
- **V5** — `get_session_firebase_tokens` appended (per-user push tokens)

Layout is **append-only** — fields are added at the end so plugins compiled against an older `DmartCallbacks` struct can still read the version they understand. Check `cb.Version >= N` before calling any field appended in V`N` (the `DmartSdk.*` C# wrappers do this automatically and fall back to a safe no-op return).

`resource_type` in `load_entry` accepts the wire form (`"content"`, `"folder"`, `"ticket"`, …) or NULL for a type-agnostic lookup.

**C# helper** — copy `custom_plugins_sdk/shared/DmartCallbacks.cs` into your plugin project. It gives you a ready-to-use `DmartCallbacks` struct plus `DmartSdk.LoadUser(_cb, shortname)` / `DmartSdk.SaveEntry(_cb, entryJson)` / etc. wrappers that handle UTF-8 marshaling and string freeing for you.

```csharp
using Dmart.Sdk;

private static DmartCallbacks _cb;
private static bool _cbReady;

[UnmanagedCallersOnly(EntryPoint = "init")]
public static unsafe void Init(IntPtr cbsPtr)
{
    _cb = Marshal.PtrToStructure<DmartCallbacks>(cbsPtr);
    _cbReady = true;
}

[UnmanagedCallersOnly(EntryPoint = "hook")]
public static IntPtr Hook(IntPtr eventJsonPtr)
{
    if (_cbReady)
    {
        var userJson = DmartSdk.LoadUser(_cb, "dmart");
        DmartSdk.SendEmail(_cb, "ops@example.com", "alert", "<p>hi</p>");
    }
    return AllocUtf8("""{"status":"ok"}""");
}
```

### C# Example

```csharp
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

public static class Plugin
{
    [UnmanagedCallersOnly(EntryPoint = "get_info")]
    public static IntPtr GetInfo()
        => AllocUtf8("""{"shortname":"my_hook","type":"hook"}""");

    [UnmanagedCallersOnly(EntryPoint = "dmart_plugin_version")]
    public static IntPtr GetVersion() => StaticVersionPtr;
    private static readonly IntPtr StaticVersionPtr =
        Marshal.StringToHGlobalAnsi("1.0.0");  // process-lifetime, never freed

    [UnmanagedCallersOnly(EntryPoint = "hook")]
    public static IntPtr Hook(IntPtr eventJsonPtr)
    {
        var json = Marshal.PtrToStringUTF8(eventJsonPtr) ?? "";
        Console.Error.WriteLine($"[my_hook] {json[..80]}");
        return AllocUtf8("""{"status":"ok"}""");
    }

    [UnmanagedCallersOnly(EntryPoint = "free_string")]
    public static void FreeString(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
    }

    static IntPtr AllocUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }
}
```

Build: `dotnet publish -c Release -r linux-x64` → `*.so`

### Rust Example

```rust
use std::ffi::{CStr, CString};
use std::os::raw::c_char;

#[no_mangle]
pub extern "C" fn get_info() -> *mut c_char {
    CString::new(r#"{"shortname":"rust_hook","type":"hook"}"#).unwrap().into_raw()
}

// Version literal lives in the binary's read-only data section and is never freed.
#[no_mangle]
pub extern "C" fn dmart_plugin_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const c_char
}

#[no_mangle]
pub extern "C" fn hook(event_json: *const c_char) -> *mut c_char {
    let json = unsafe { CStr::from_ptr(event_json).to_str().unwrap_or("") };
    eprintln!("[rust_hook] {}", &json[..80.min(json.len())]);
    CString::new(r#"{"status":"ok"}"#).unwrap().into_raw()
}

#[no_mangle]
pub extern "C" fn free_string(ptr: *mut c_char) {
    if !ptr.is_null() { unsafe { drop(CString::from_raw(ptr)); } }
}
```

Build: `cargo build --release` → `target/release/librust_hook.so`

### C Example

```c
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

char* get_info() { return strdup("{\"shortname\":\"c_hook\",\"type\":\"hook\"}"); }
char* hook(const char* event_json) { fprintf(stderr, "[c_hook] event\n"); return strdup("{\"status\":\"ok\"}"); }
void free_string(char* ptr) { free(ptr); }

// Optional. Define PLUGIN_VERSION at build time:
//   gcc -shared -fPIC -DPLUGIN_VERSION=\"1.0.0\" -o c_hook.so plugin.c
const char* dmart_plugin_version(void) { return PLUGIN_VERSION; }
```

Build: `gcc -shared -fPIC -DPLUGIN_VERSION=\"1.0.0\" -o c_hook.so plugin.c`

---

## Event Object (what hook plugins receive)

```json
{
  "space_name": "myspace",
  "subpath": "posts",
  "shortname": "my_entry",
  "action_type": "create",
  "resource_type": "content",
  "schema_shortname": "blog_post",
  "user_shortname": "admin",
  "attributes": {}
}
```

## Request Object (what API plugins receive)

```json
{
  "method": "GET",
  "path": "/my_api/greet/alice",
  "query": {"key": "value"},
  "headers": {"Authorization": "Bearer ..."},
  "body": null,
  "user": "admin"
}
```

## config.json Reference

```json
{
  "shortname": "my_plugin",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "ordinal": 100,
  "concurrent": true,
  "filters": {
    "subpaths": { "__all_spaces__": ["__all_subpaths__"] },
    "resource_types": ["content"],
    "schema_shortnames": [],
    "actions": ["create", "update", "delete"]
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `shortname` | string | Must match directory name |
| `is_active` | bool | `false` = not loaded |
| `type` | `"hook"` or `"api"` | Plugin type |
| `listen_time` | `"before"` or `"after"` | Hook only. `before` can abort actions |
| `ordinal` | int | Execution order (lower = first, default 9999) |
| `concurrent` | bool | After-hooks: `true` = fire-and-forget (default) |
| `filters.subpaths` | object | Per-space subpath dict — see "Filter shape" below |
| `filters.resource_types` | string[] | Empty = all, or list specific resource types |
| `filters.schema_shortnames` | string[] | Empty = all, or list specific content schemas |
| `filters.actions` | string[] | Empty = all, or: create, update, delete, move, lock, unlock, etc. |

## Filter shape (permission-style)

`filters.subpaths` is a **dictionary** keyed by space name. The same
vocabulary the permission engine uses:

| Sentinel | Meaning |
|----------|---------|
| `"__all_spaces__"` (as a key) | Match any space |
| `"__all_subpaths__"` (in the value list) | Match any subpath under that space |
| `"__current_user__"` (inside a pattern) | Replaced by the event's `user_shortname` |

```json
"filters": {
  "subpaths": {
    "myspace":          ["tickets", "issues"],
    "shared":           ["__all_subpaths__"],
    "__all_spaces__":   ["public"]
  },
  "resource_types":   ["content"],
  "schema_shortnames": ["bug_report"],
  "actions":          ["create", "update"]
}
```

A subpath pattern is a hierarchical prefix: `"tickets"` matches event
subpaths `"tickets"`, `"tickets/open"`, `"tickets/open/p1"` — but NOT
`"ticketsearch"`. An **empty** `subpaths` dict means the plugin doesn't
fire on any event; explicitly opt in to "everything" with
`{ "__all_spaces__": ["__all_subpaths__"] }`.

Empty `resource_types` / `schema_shortnames` / `actions` lists each
mean "match every value of that dimension" — same convention as
permissions. `schema_shortnames` is only consulted when the event's
`resource_type` is `content`.

### Migrating from the legacy flat-array shape

Configs that still use `"subpaths": ["__ALL__"]` or `"__ALL__"` in
`resource_types` / `schema_shortnames` will be **rejected at load** with
a clear migration error. Convert:

```diff
-"subpaths": ["__ALL__"]
+"subpaths": { "__all_spaces__": ["__all_subpaths__"] }

-"resource_types": ["__ALL__"]
+"resource_types": []

-"schema_shortnames": ["__ALL__"]
+"schema_shortnames": []
```

The `always_active` flag (used to bypass the old per-space
`active_plugins` opt-in) is gone — every plugin now self-declares
its scope.

## Hook Lifecycle

```
Client request
  │
  ▼
Before hooks (listen_time: "before")
  │ Plugin returns error → ACTION ABORTED
  │
  ▼
Action executes (create/update/delete/...)
  │
  ▼
After hooks (listen_time: "after")
  │ concurrent=true  → fire-and-forget (failures logged)
  │ concurrent=false → awaited (failures logged, don't fail action)
```

The per-space `active_plugins` opt-in list **no longer exists** — a
plugin fires for every event matched by its own `filters` block. The
field on the `spaces` table is left in place for back-compat with older
servers but is no longer read or written.

API plugins ignore `filters` entirely — routes are mounted if `is_active: true`.

## Directory Layout

```
~/.dmart/plugins/
  my_subprocess_hook/
    config.json
    my_subprocess_hook     ← executable (subprocess mode, crash-safe)
  my_so_hook/
    config.json
    my_so_hook.so          ← shared library (in-process mode, fastest)
  my_api/
    config.json
    my_api                 ← executable or .so
```

## Sample Projects

Working examples in this directory:

| Directory | Mode | Language | Type |
|-----------|------|----------|------|
| `sample_hook/` | In-process .so | C# | Hook |
| `sample_api/` | In-process .so | C# | API |
| `sample_subprocess/` | Subprocess | Python | Hook |
