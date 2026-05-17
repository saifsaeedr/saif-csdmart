#!/bin/bash
set -e

# Build the dmart APK package for Alpine.
#
# Runs the NativeAOT publish + apk packaging inside an
# `mcr.microsoft.com/dotnet/sdk:10.0-alpine` container so the resulting
# binary is musl-linked and runs natively on Alpine.
#
# Usage:
#   ./dist/build-apk.sh                       # x86_64 (default)
#   ./dist/build-apk.sh --arch x86_64
#   ./dist/build-apk.sh --arch aarch64
#   VERSION=1.2.3 ./dist/build-apk.sh         # explicit version override
#
# Host requirements:
#   - podman OR docker (CONTAINER_ENGINE env overrides)
#   - UI dists pre-built (cxb/dist/client + catalog/dist/client) or
#     yarn/npm on PATH for ./build-ui.sh to run
#   - For cross-arch builds (host arch != target arch), qemu-user-static
#     must be registered with binfmt_misc. The script auto-detects and
#     passes --platform to the container engine when needed.

cd "$(dirname "$0")/.."
SRCDIR="$(pwd)"

ARCH="x86_64"
while [[ $# -gt 0 ]]; do
	case "$1" in
		--arch)   ARCH="$2"; shift 2 ;;
		--arch=*) ARCH="${1#*=}"; shift ;;
		-h|--help)
			cat <<-EOF
			Usage: $0 [--arch x86_64|aarch64]
			  --arch x86_64     produce dmart-<v>-x86_64.apk (linux-musl-x64)
			  --arch aarch64    produce dmart-<v>-aarch64.apk (linux-musl-arm64)
			Environment:
			  VERSION           explicit version (default: from git describe)
			  CONTAINER_ENGINE  podman | docker (default: podman)
			EOF
			exit 0 ;;
		*) echo "Unknown arg: $1 (try --help)" >&2; exit 2 ;;
	esac
done

# Map Alpine arch → .NET RID → OCI platform string.
# Alpine uses x86_64/aarch64; .NET uses x64/arm64; OCI uses amd64/arm64.
case "$ARCH" in
	x86_64)  RID="linux-musl-x64";   OCI_PLATFORM="linux/amd64" ;;
	aarch64) RID="linux-musl-arm64"; OCI_PLATFORM="linux/arm64" ;;
	*) echo "Unsupported arch: $ARCH (use x86_64 or aarch64)" >&2; exit 2 ;;
esac

# Version derivation — mirrors dist/build-rpm.sh so RPMs and APKs cut
# from the same commit get identical version strings.
if [ -z "$VERSION" ]; then
	GIT_DESC=$(git describe --tags 2>/dev/null || echo "v0.1.0")
	BASE_VER=$(echo "$GIT_DESC" | cut -d '-' -f 1 | sed 's/^v//')
	MINOR=$(echo "$GIT_DESC" | cut -d '-' -f 2 -s)
	VERSION="${BASE_VER}${MINOR:+.$MINOR}"
fi

echo "Building dmart-${VERSION} APK for ${ARCH}..."

# UI dists must exist before the container runs — the Alpine SDK image
# has no Node.js toolchain. Build on the host if missing; CI pre-extracts
# from the shared ui-tarballs artifact and skips this path.
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

# Auto-detect cross-arch and pass --platform only when needed. On CI
# (self-hosted x86_64 → x86_64 apk, ubuntu-24.04-arm → aarch64 apk)
# host == target, so this is a no-op and there's no QEMU overhead.
HOST_ARCH=$(uname -m)
PLATFORM_FLAG=""
if [ "$HOST_ARCH" != "$ARCH" ]; then
	PLATFORM_FLAG="--platform=${OCI_PLATFORM}"
	echo "Cross-arch build: host=$HOST_ARCH target=$ARCH (using QEMU via $PLATFORM_FLAG)"
fi

# Persistent NuGet cache shared with host-side dotnet so cold runs
# don't re-download every package. Matches dist/build-rpm.sh's pattern.
HOST_NUGET_CACHE="${HOME}/.nuget/packages"
mkdir -p "$HOST_NUGET_CACHE" dist/out

# Wipe prior bin/obj for this RID so a stale linux-x64 build doesn't
# leak into the musl publish. Other RIDs' outputs are left alone.
rm -rf "bin/Release/net10.0/${RID}" "obj/Release/net10.0/${RID}"

# Single container invocation: install toolchain → AOT publish →
# stage APKBUILD inputs → abuild → copy .apk out. Running as root
# inside the container is fine because abuild's `-F` flag tolerates
# it (it's the documented escape hatch for CI/container builds).
$ENGINE run --rm $PLATFORM_FLAG \
	--network=host \
	-v "${SRCDIR}:/src:z" \
	-v "${HOST_NUGET_CACHE}:/nuget-packages:z" \
	-e VERSION="$VERSION" \
	-e ARCH="$ARCH" \
	-e RID="$RID" \
	-e NUGET_PACKAGES=/nuget-packages \
	-e HOME=/root \
	-w /src \
	mcr.microsoft.com/dotnet/sdk:10.0-alpine \
	sh -c '
		set -e

		# alpine-sdk pulls abuild, build-base, fakeroot etc.
		# clang + lld are the NativeAOT linker toolchain.
		# zlib-static is required because NativeAOT statically links
		# zlib for System.IO.Compression — zlib-dev alone gives the
		# headers but not the .a archive, so the link fails with
		# "cannot find -lz" and a misleading error.
		# git is needed for ./build.sh git-describe version stamping.
		# jq is the runtime dependency declared in APKBUILD; abuild
		# folds runtime deps into its builddeps virtual package for
		# install-validation, so installing it ahead of time avoids
		# a second apk-update round-trip during `abuild -r`.
		apk add --no-cache --quiet \
			abuild alpine-sdk clang lld zlib-dev zlib-static git jq

		# AOT publish. build.sh handles the InformationalVersion
		# stamping (git describe + branch + date) so the same logic
		# runs in CI, local builds, and inside this container.
		sh ./build.sh --aot --rid "$RID"

		# Stage APKBUILD inputs in a clean directory. abuild treats
		# the parent dir name as the repo name; calling it "apkbuild"
		# gives us a predictable path under ~/packages later.
		APKROOT=/tmp/apkbuild
		rm -rf "$APKROOT" && mkdir -p "$APKROOT"

		cp bin/Release/net10.0/$RID/publish/dmart "$APKROOT/dmart"
		cp dist/dmart.service                     "$APKROOT/"
		cp dist/apk/dmart.openrc-init             "$APKROOT/"
		cp dist/dmart.bash dist/dmart.fish        "$APKROOT/"
		cp config.env.sample                      "$APKROOT/"

		# Plugin configs bundled as a tarball so the APKBUILD has one
		# named source entry instead of a moving glob. Extracted into
		# /usr/lib/dmart/plugins/ inside package().
		tar -czf "$APKROOT/plugins.tar.gz" -C plugins .

		# Render APKBUILD from template with version + arch.
		sed -e "s|__VERSION__|$VERSION|g" \
		    -e "s|__ARCH__|$ARCH|g" \
		    dist/apk/APKBUILD.in > "$APKROOT/APKBUILD"

		# Install scripts — names follow Alpine convention so abuild
		# packs them as triggers when listed in $install.
		cp dist/apk/dmart.pre-install dist/apk/dmart.post-install \
		   dist/apk/dmart.post-deinstall \
		   "$APKROOT/"

		cd "$APKROOT"

		# Ephemeral signing key — fresh per build, never persisted.
		# Downloaders use `apk add --allow-untrusted dmart-*.apk`.
		# -a = append to abuild.conf, -n = no email prompt. Skipping -i
		# (install pubkey) because that invokes doas/sudo, which the
		# Alpine SDK image does not ship; we are already root, so the
		# install is a plain `cp`.
		abuild-keygen -a -n >/dev/null
		cp /root/.abuild/*.rsa.pub /etc/apk/keys/

		# Regenerate sha512sums for the source list, then build.
		# -F = run as root (default refusal is for dev machines).
		abuild -F checksum
		abuild -F -r

		# abuild drops the .apk at ~/packages/<repo>/<arch>/. The repo
		# name is the *grandparent* of APKBUILD, not the parent — i.e.
		# our /tmp/apkbuild/APKBUILD → repo="tmp", arch=x86_64. Find by
		# name instead of hard-coding the path so the layout convention
		# is not load-bearing on this script.
		APK_OUT=$(find /root/packages -type f -name "dmart-$VERSION-r0.apk" | head -1)
		[ -n "$APK_OUT" ] || {
			echo "No dmart-$VERSION-r0.apk under /root/packages:"
			find /root/packages -type f 2>/dev/null
			exit 1
		}

		# Final name embeds the arch so the two release jobs do not
		# collide when both upload to the same release tag.
		cp "$APK_OUT" "/src/dist/out/dmart-$VERSION-$ARCH.apk"
	'

echo ""
echo "=== APK built ==="
ls -lh "dist/out/dmart-${VERSION}-${ARCH}.apk"
