#!/bin/bash
set -e

# Build the dmart Debian .deb package (amd64).
#
# Runs the NativeAOT publish + dpkg-deb packaging inside an
# `mcr.microsoft.com/dotnet/sdk:10.0` container (Debian 12 / bookworm
# based) so the resulting binary is glibc-2.36-linked and runs on
# Debian 12+ / Ubuntu 24.04+. Older glibc targets need a different
# base image — out of scope here; rebuild from source on the target
# distro if you need that.
#
# Usage:
#   ./dist/build-deb.sh                  # uses git describe for version
#   VERSION=1.2.3 ./dist/build-deb.sh    # explicit version
#
# Host requirements:
#   - podman OR docker (CONTAINER_ENGINE env overrides)
#   - UI dists pre-built (cxb/dist/client + catalog/dist/client) or
#     yarn/npm on PATH for ./build-ui.sh to run

cd "$(dirname "$0")/.."
SRCDIR="$(pwd)"

# Version derivation mirrors dist/build-rpm.sh + dist/build-apk.sh so .deb
# / .rpm / .apk cut from the same commit get identical version strings.
if [ -z "$VERSION" ]; then
    GIT_DESC=$(git describe --tags 2>/dev/null || echo "v0.1.0")
    BASE_VER=$(echo "$GIT_DESC" | cut -d '-' -f 1 | sed 's/^v//')
    MINOR=$(echo "$GIT_DESC" | cut -d '-' -f 2 -s)
    VERSION="${BASE_VER}${MINOR:+.$MINOR}"
fi

echo "Building dmart_${VERSION}_amd64.deb..."

# UI dists must exist before the container runs — the dotnet SDK image
# has no Node.js. Build on the host if missing; CI pre-extracts from the
# shared ui-tarballs artifact and skips this path.
needs_ui=false
[ -f cxb/package.json ]     && [ ! -f cxb/dist/client/index.html ]     && needs_ui=true
[ -f catalog/package.json ] && [ ! -f catalog/dist/client/index.html ] && needs_ui=true
if [ "$needs_ui" = true ]; then
    echo "Building UI frontends locally (pre-container)..."
    ./build-ui.sh || { echo "UI build failed" >&2; exit 1; }
else
    echo "UI frontends ready (dists present or sources absent), skipping"
fi

ENGINE="${CONTAINER_ENGINE:-podman}"
command -v "$ENGINE" >/dev/null 2>&1 || {
    echo "Container engine '$ENGINE' not found on PATH" >&2; exit 1;
}

# Persistent NuGet cache shared with host-side dotnet so cold runs don't
# re-download every package. Matches dist/build-rpm.sh + dist/build-apk.sh.
HOST_NUGET_CACHE="${HOME}/.nuget/packages"
mkdir -p "$HOST_NUGET_CACHE" dist/out

# Clean prior linux-x64 bin/obj so a stale build doesn't leak into the
# .deb. Other RID outputs (osx-arm64, linux-musl-x64, ...) are left alone.
rm -rf "bin/Release/net10.0/linux-x64" "obj/Release/net10.0/linux-x64"

# Single container shot: install dpkg-dev + clang on top of the dotnet SDK
# image, run ./build.sh --aot, assemble the install tree, dpkg-deb --build.
# The mcr.microsoft.com/dotnet/sdk:10.0 image is Debian-12-based and ships
# dotnet pre-installed — saves the 3-min `dnf install dotnet-sdk` step
# that dist/build-rpm.sh's almalinux path pays.
$ENGINE run --rm \
    --network=host \
    -v "${SRCDIR}:/src:z" \
    -v "${HOST_NUGET_CACHE}:/nuget-packages:z" \
    -e VERSION="$VERSION" \
    -e NUGET_PACKAGES=/nuget-packages \
    -e HOME=/root \
    -w /src \
    debian:12-slim bash -c '
        set -euo pipefail
        # Why debian:12-slim instead of mcr.microsoft.com/dotnet/sdk:10.0:
        # the official .NET 10 SDK images are Ubuntu 24.04 (Noble) only —
        # no Debian-12 variant ships. An AOT binary built on Noble
        # (glibc 2.39) refuses to start on Debian 12 (glibc 2.36) with
        # "GLIBC_2.38 not found". To target the broadest .deb audience
        # (Debian 12+ AND Ubuntu 22.04+) we build against the older
        # bookworm glibc. Microsoft maintains a dotnet-sdk-10.0 package
        # in its Debian-12 apt feed precisely for this case.
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq
        # dpkg-dev gives us dpkg-deb. clang + zlib1g-dev are the
        # NativeAOT linker toolchain. git is for ./build.sh git-describe.
        # ca-certificates + curl + gnupg are needed to fetch and trust
        # the MS apt feed signing key.
        apt-get install -y --no-install-recommends \
            ca-certificates curl gnupg \
            dpkg-dev clang zlib1g-dev git xz-utils >/dev/null

        # MS apt feed for .NET 10 on Debian 12 (bookworm). The
        # signed-by= path scopes trust to this single keyring; default
        # apt-key behaviour would trust the MS key for every repo.
        curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
            | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg
        echo "deb [signed-by=/usr/share/keyrings/microsoft.gpg] \
              https://packages.microsoft.com/debian/12/prod bookworm main" \
            > /etc/apt/sources.list.d/microsoft.list
        apt-get update -qq
        apt-get install -y --no-install-recommends dotnet-sdk-10.0 >/dev/null

        # AOT publish. build.sh uses `[[` (bash extension), so invoke
        # through bash rather than sh — sh would silently fall through
        # to the default `fast` JIT path. build.sh handles the
        # InformationalVersion stamping (git describe + branch + date).
        bash ./build.sh --aot --rid linux-x64

        # Assemble the install layout under STAGE. Mirrors dmart.spec %install
        # block file-for-file so /usr/bin/dmart, /usr/lib/dmart/plugins/,
        # /etc/dmart, /var/lib/dmart, /usr/lib/systemd/system/dmart.service,
        # and the shell completions all land where the .rpm puts them.
        STAGE=/tmp/dmart-deb
        rm -rf "$STAGE" && mkdir -p "$STAGE/DEBIAN" \
            "$STAGE/usr/bin" \
            "$STAGE/usr/lib/dmart/plugins" \
            "$STAGE/usr/lib/systemd/system" \
            "$STAGE/etc/dmart" \
            "$STAGE/etc/bash_completion.d" \
            "$STAGE/usr/share/fish/vendor_completions.d" \
            "$STAGE/usr/share/dmart" \
            "$STAGE/var/lib/dmart/spaces" \
            "$STAGE/var/lib/dmart/custom_plugins"

        install -D -m 0755 bin/dmart "$STAGE/usr/bin/dmart"

        # Plugin configs — one config.json per plugin folder.
        for d in plugins/*/; do
            n=$(basename "$d")
            install -D -m 0644 "$d/config.json" \
                "$STAGE/usr/lib/dmart/plugins/$n/config.json"
        done

        install -D -m 0644 config.env.sample      "$STAGE/usr/share/dmart/config.env.sample"
        # Ship the initial /etc/dmart/config.env as a conffile (see
        # DEBIAN/conffiles below). dpkg only manages files it actually
        # ships, so the empty initial copy must be in the .deb tree;
        # postinst chowns it root:dmart 0640 after extraction.
        install -D -m 0640 config.env.sample      "$STAGE/etc/dmart/config.env"
        install -D -m 0644 dist/dmart.service     "$STAGE/usr/lib/systemd/system/dmart.service"
        install -D -m 0644 dist/dmart.bash        "$STAGE/etc/bash_completion.d/dmart"
        install -D -m 0644 dist/dmart.fish        "$STAGE/usr/share/fish/vendor_completions.d/dmart.fish"

        # Render control from template + install maintainer scripts.
        sed "s|__VERSION__|$VERSION|" dist/debian/control.in > "$STAGE/DEBIAN/control"
        install -m 0755 dist/debian/postinst "$STAGE/DEBIAN/postinst"
        install -m 0755 dist/debian/prerm    "$STAGE/DEBIAN/prerm"
        install -m 0755 dist/debian/postrm   "$STAGE/DEBIAN/postrm"

        # Conffiles — dpkg will prompt the operator before overwriting these
        # on upgrade. /etc/dmart/config.env is the seeded copy of the sample;
        # operator edits must survive `apt upgrade`.
        cat > "$STAGE/DEBIAN/conffiles" <<EOF
/etc/dmart/config.env
EOF

        # -Zxz: xz compression, the default for modern Debian (smaller .deb
        # than gzip; dpkg has supported xz since wheezy). --root-owner-group:
        # force root:root inside the .deb regardless of who ran dpkg-deb.
        dpkg-deb --root-owner-group -Zxz --build "$STAGE" \
            "/src/dist/out/dmart_${VERSION}_amd64.deb"
    '

echo ""
echo "=== .deb built ==="
ls -lh "dist/out/dmart_${VERSION}_amd64.deb"
