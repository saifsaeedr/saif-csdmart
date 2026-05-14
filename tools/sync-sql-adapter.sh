#!/usr/bin/env bash
# sync-sql-adapter.sh — refresh a vendored copy of Dmart.SqlAdapter from this
# repo's canonical version. csdmart's Dmart.SqlAdapter/ is the source of
# truth; downstream mirrors (today: DmartMDWDemo) are derived artifacts.
#
# Usage:
#   DMART_DEMO_PATH=/path/to/DmartMDWDemo/DmartMDWDemo  tools/sync-sql-adapter.sh
#
# Run from the csdmart repo root (where Dmart.SqlAdapter/ lives). The
# command no-ops if DMART_DEMO_PATH isn't set — that's intentional so
# `find . -type f -name 'sync-*' -exec {} \;` style automations don't
# pin the path.

set -euo pipefail

SRC_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)/Dmart.SqlAdapter"

if [[ ! -d "$SRC_DIR" ]]; then
    echo "error: expected canonical SDK at $SRC_DIR" >&2
    exit 1
fi

if [[ -z "${DMART_DEMO_PATH:-}" ]]; then
    echo "DMART_DEMO_PATH not set; nothing to sync." >&2
    echo "Hint: export DMART_DEMO_PATH=/path/to/DmartMDWDemo/DmartMDWDemo" >&2
    exit 0
fi

DEST_DIR="$DMART_DEMO_PATH/Dmart.SqlAdapter"
if [[ ! -d "$DEST_DIR" ]]; then
    echo "error: destination $DEST_DIR does not exist." >&2
    echo "Either the demo path is wrong, or this is a first-time sync that needs the directory created manually." >&2
    exit 1
fi

# Hand-list the source subdirectories + files so this stays predictable as
# the SDK grows. UPDATE THIS LIST when adding a new top-level folder under
# Dmart.SqlAdapter/.
SUBDIRS=(Helpers Permissions)
TOP_FILES=("$SRC_DIR"/*.cs "$SRC_DIR"/*.csproj "$SRC_DIR/README.md")

echo "syncing $SRC_DIR -> $DEST_DIR"
for sub in "${SUBDIRS[@]}"; do
    if [[ -d "$SRC_DIR/$sub" ]]; then
        rm -rf "${DEST_DIR:?}/$sub"
        cp -r "$SRC_DIR/$sub" "$DEST_DIR/"
        echo "  ✓ $sub/"
    fi
done
for f in "${TOP_FILES[@]}"; do
    if [[ -f "$f" ]]; then
        cp "$f" "$DEST_DIR/$(basename "$f")"
        echo "  ✓ $(basename "$f")"
    fi
done

echo "done."
