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
← stdout: {"shortname":"my_plugin","type":"hook"}

→ stdin:  {"type":"hook","event":{...}}
← stdout: {"status":"ok"}

→ stdin:  {"type":"request","request":{...}}
← stdout: {"status":"success","attributes":{...}}
```

Debug output goes to stderr (forwarded to dmart's console).

### Quick Start (Python)

```python
#!/usr/bin/env python3
import json, sys

for line in sys.stdin:
    msg = json.loads(line.strip())

    if msg["type"] == "info":
        print(json.dumps({"shortname": "my_hook", "type": "hook"}), flush=True)

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
        info)    echo '{"shortname":"bash_hook","type":"hook"}' ;;
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
        console.log(JSON.stringify({shortname: 'node_hook', type: 'hook'}));
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

# Add config
cat > ~/.dmart/plugins/my_hook/config.json << 'EOF'
{
  "shortname": "my_hook",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "filters": {
    "subpaths": ["__ALL__"],
    "resource_types": ["content"],
    "schema_shortnames": ["__ALL__"],
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

All strings are null-terminated UTF-8. The plugin allocates return strings; dmart calls `free_string()` to release them.

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
```

Build: `gcc -shared -fPIC -o c_hook.so plugin.c`

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
    "subpaths": ["__ALL__"],
    "resource_types": ["content"],
    "schema_shortnames": ["__ALL__"],
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
| `filters.subpaths` | string[] | `"__ALL__"` or specific paths |
| `filters.resource_types` | string[] | `"__ALL__"` or: content, folder, schema, user, etc. |
| `filters.schema_shortnames` | string[] | `"__ALL__"` or specific schemas |
| `filters.actions` | string[] | create, update, delete, move, lock, unlock, etc. |

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

Hook plugins only fire for spaces that list them in `active_plugins`:

```bash
curl -X POST http://localhost:5099/managed/request \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"space_name":"myspace","request_type":"update","records":[{
    "resource_type":"space","subpath":"/","shortname":"myspace",
    "attributes":{"active_plugins":["my_hook","resource_folders_creation"]}
  }]}'
```

API plugins do NOT need per-space activation — routes are always mounted if `is_active: true`.

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
