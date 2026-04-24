#!/bin/bash
set -e

# Build the dmart RPM package.
#
# Prerequisites (native build):
#   dnf install rpm-build dotnet-sdk-10.0 clang zlib-devel
#
# Usage:
#   ./dist/build-rpm.sh              # build for current OS (e.g. Fedora)
#   ./dist/build-rpm.sh el9          # build for RHEL 9 / AlmaLinux 9 via podman
#   ./dist/build-rpm.sh srpm         # source RPM only (no binary build)
#   VERSION=1.0.0 ./dist/build-rpm.sh srpm   # explicit version
#   VERSION=1.0.0 ./dist/build-rpm.sh el9   # explicit version

cd "$(dirname "$0")/.."
SRCDIR="$(pwd)"

TARGET="${1:-}"

# Source RPM only — no binary build needed
if [[ "$TARGET" == "srpm" ]]; then
    if [ -z "$VERSION" ]; then
        GIT_DESC=$(git describe --tags 2>/dev/null || echo "v0.1.0")
        BASE_VER=$(echo "$GIT_DESC" | cut -d '-' -f 1 | sed 's/^v//')
        MINOR=$(echo "$GIT_DESC" | cut -d '-' -f 2 -s)
        VERSION="${BASE_VER}${MINOR:+.$MINOR}"
    fi
    echo "Building dmart-${VERSION} SRPM..."

    STAGING=$(mktemp -d)
    TARDIR="$STAGING/dmart-${VERSION}"
    mkdir -p "$TARDIR"

    # Package full source tree (for dotnet publish inside rpmbuild %build)
    cp -r Plugins/ Models/ Services/ Config/ DataAdapters/ Api/ Middleware/ \
          Cli/ Auth/ Utils/ "$TARDIR/" 2>/dev/null || true
    cp dmart.csproj dmart.slnx Program.cs config.env.sample \
       Directory.Build.props Directory.Packages.props 2>/dev/null "$TARDIR/" || true
    # NuGet config if present
    cp nuget.config "$TARDIR/" 2>/dev/null || true
    # Plugin configs
    cp -r plugins/ "$TARDIR/"
    # CXB dist (pre-built frontend)
    if [ -d cxb/dist/client ]; then
        mkdir -p "$TARDIR/cxb/dist"
        cp -r cxb/dist/client "$TARDIR/cxb/dist/"
    fi
    # Dist files (service, completions)
    cp dist/dmart.service dist/dmart.bash dist/dmart.fish "$TARDIR/"

    tar -czf "$STAGING/dmart-${VERSION}.tar.gz" -C "$STAGING" "dmart-${VERSION}"

    RPMBUILD=$(mktemp -d)
    mkdir -p "$RPMBUILD"/{SOURCES,SPECS,SRPMS}
    cp "$STAGING/dmart-${VERSION}.tar.gz" "$RPMBUILD/SOURCES/"
    sed "s/^Version:.*$/Version:        ${VERSION}/" dist/dmart.spec > "$RPMBUILD/SPECS/dmart.spec"

    rpmbuild -bs \
        --define "_topdir $RPMBUILD" \
        --define "dist %{nil}" \
        "$RPMBUILD/SPECS/dmart.spec"

    mkdir -p "$SRCDIR/dist/out"
    # Keep only one SRPM on disk — older versions would otherwise pile up in
    # dist/out/ and confuse downstream scripts globbing for the current build.
    rm -f "$SRCDIR/dist/out/"*.src.rpm
    cp "$RPMBUILD"/SRPMS/*.src.rpm "$SRCDIR/dist/out/"

    echo ""
    echo "=== SRPM built ==="
    ls -lh "$SRCDIR/dist/out/"*.src.rpm
    rm -rf "$STAGING" "$RPMBUILD"
    exit 0
fi

# Build inside a container for cross-distro targets
if [[ "$TARGET" == "el9" || "$TARGET" == "rhel9" ]]; then
    ENGINE="${CONTAINER_ENGINE:-podman}"
    if [ -z "$VERSION" ]; then
        GIT_DESC=$(git describe --tags 2>/dev/null || echo "v0.1.0")
        BASE_VER=$(echo "$GIT_DESC" | cut -d '-' -f 1 | sed 's/^v//')
        MINOR=$(echo "$GIT_DESC" | cut -d '-' -f 2 -s)
        VERSION="${BASE_VER}${MINOR:+.$MINOR}"
    fi
    echo "Building dmart-${VERSION} RPM for RHEL 9 via ${ENGINE}..."
    # Build UI frontends on the host (the el9 container has no Node.js — it
    # only ships dotnet-sdk + rpm-build + clang). Skip per-SPA when a dist is
    # already present — CI pre-extracts dists from the shared ui-tarballs
    # artifact, so rebuilding them here would be ~95s of wasted work per job.
    needs_ui=false
    [ -f cxb/package.json ]     && [ ! -f cxb/dist/client/index.html ]     && needs_ui=true
    [ -f catalog/package.json ] && [ ! -f catalog/dist/client/index.html ] && needs_ui=true
    if [ "$needs_ui" = true ]; then
        echo "Building UI frontends locally (pre-container)..."
        ./build-ui.sh || { echo "UI build failed" >&2; exit 1; }
    else
        echo "UI frontends ready (dists present or sources absent), skipping"
    fi
    mkdir -p dist/out
    CONTAINER_NAME="dmart-el9-builder"
    # Check if builder container exists and is usable
    NEED_CREATE=true
    if $ENGINE container exists "$CONTAINER_NAME" 2>/dev/null; then
        # Try to start it — if it runs, we can reuse it
        $ENGINE start "$CONTAINER_NAME" 2>/dev/null || true
        sleep 1
        if $ENGINE exec "$CONTAINER_NAME" true 2>/dev/null; then
            # Verify DNS works — a stale container created under a different
            # host network config can have a broken /etc/resolv.conf that
            # survives restarts. If DNS is dead we recreate rather than hand
            # the user a cryptic NuGet restore failure downstream.
            if ! $ENGINE exec "$CONTAINER_NAME" getent hosts api.nuget.org >/dev/null 2>&1; then
                echo "Container $CONTAINER_NAME has broken DNS, recreating..."
                $ENGINE rm -f "$CONTAINER_NAME" 2>/dev/null || true
            # Verify the container can read every top-level source dir that
            # the publish step needs. Old :Z-labeled bind mounts don't
            # relabel files added to the host after container creation, so
            # a container born before catalog/ existed can't see it today
            # and the EmbeddedResource glob silently matches zero files.
            # Detect that mismatch once per run and recreate so the new
            # container uses :z (shared label) on a clean slate.
            elif ([ -d cxb ]     && ! $ENGINE exec "$CONTAINER_NAME" test -r /src/cxb/package.json     2>/dev/null) ||
                 ([ -d catalog ] && ! $ENGINE exec "$CONTAINER_NAME" test -r /src/catalog/package.json 2>/dev/null); then
                echo "Container $CONTAINER_NAME can't read all source dirs (likely :Z label drift), recreating..."
                $ENGINE rm -f "$CONTAINER_NAME" 2>/dev/null || true
            # Verify the SDK is installed. A container that was created but
            # had its SDK install step aborted (e.g. crun failed during init
            # on the prior build attempt and left a bare almalinux shell
            # behind) will pass the DNS + source-dir checks and still blow
            # up at the first `dotnet` call downstream. Cheap probe: run
            # `dotnet --version` — succeeds iff the SDK is actually there.
            elif ! $ENGINE exec "$CONTAINER_NAME" bash -c 'command -v dotnet >/dev/null' 2>/dev/null; then
                echo "Container $CONTAINER_NAME has no dotnet (SDK install never completed), recreating..."
                $ENGINE rm -f "$CONTAINER_NAME" 2>/dev/null || true
            else
                echo "Reusing existing $CONTAINER_NAME container..."
                NEED_CREATE=false
            fi
        else
            echo "Container $CONTAINER_NAME is dead, recreating..."
            $ENGINE rm -f "$CONTAINER_NAME" 2>/dev/null || true
        fi
    fi
    # Persistent NuGet cache on the host. Without this, container rebuilds
    # (or a cleared /tmp inside the container) force a full re-download of
    # every package on the next build. Sharing the host's ~/.nuget/packages
    # also means host-side `dotnet build` and container `dotnet publish`
    # reuse each other's downloads.
    HOST_NUGET_CACHE="${HOME}/.nuget/packages"
    mkdir -p "$HOST_NUGET_CACHE"

    if [ "$NEED_CREATE" = true ]; then
        echo "Creating $CONTAINER_NAME container (first time — installs SDK)..."
        # Bind-mount /etc/resolv.conf read-only so the container's DNS
        # tracks the host live instead of whatever was baked at creation
        # time — otherwise a long-lived builder container keeps a stale
        # resolver list and NuGet restore starts failing with "Name or
        # service not known" after the host's DNS changes.
        # Use :z (shared SELinux label, lowercase) rather than :Z (private,
        # uppercase). `:Z` relabels files only at creation time — new files
        # added to the host afterwards stay at the host's default context and
        # the container can't read them, which silently drops them from
        # EmbeddedResource globs (hit this when catalog/ was added to a repo
        # whose builder container predated it). `:z` tags the mount with the
        # shared `container_file_t` type so any file visible on the host is
        # visible in the container forever.
        # --replace unconditionally removes any container already squatting
        # on this name. Covers the case where `container exists` returned
        # non-zero but the name is still reserved by an "external entity"
        # (podman's container registry and name registry can drift apart
        # after storage corruption, interrupted builds, or a prior podman
        # unshare / system migrate that left stray lock records). Without
        # --replace the run fails with "name already in use" and the script
        # aborts before the SDK gets installed.
        $ENGINE run -d --replace \
            --name "$CONTAINER_NAME" \
            --userns=keep-id \
            --network=host \
            -v "${SRCDIR}:/src:z" \
            -v "${HOST_NUGET_CACHE}:/nuget-packages:z" \
            -v /etc/resolv.conf:/etc/resolv.conf:ro \
            -w /src \
            almalinux:9 \
            tail -f /dev/null
        $ENGINE exec --user root "$CONTAINER_NAME" bash -c '
            rpm -Uvh https://packages.microsoft.com/config/rhel/9/packages-microsoft-prod.rpm &&
            dnf install -y dotnet-sdk-10.0 rpm-build clang zlib-devel git --nobest
        '
    fi
    # Clean previous build output to force recompilation against el9 glibc
    rm -rf bin/Release obj/Release
    $ENGINE exec \
        -e VERSION="$VERSION" \
        -e HOME=/tmp \
        -e NUGET_PACKAGES=/nuget-packages \
        -w /src \
        "$CONTAINER_NAME" \
        bash /src/dist/build-rpm.sh

    # Stop the builder so it stops showing up in `podman ps` / `ps aux`. The
    # writable layer (installed dotnet-sdk, rpm-build, etc.) persists, so the
    # next build just `podman start`s it — no re-install needed. 1-second
    # timeout is plenty since PID 1 is `tail -f /dev/null`; matches the
    # alpine builder in admin_scripts/docker/notes.sh.
    $ENGINE stop -t 1 "$CONTAINER_NAME" >/dev/null 2>&1 || true

    echo ""
    echo "=== RHEL 9 RPM ==="
    ls -lh dist/out/*el9*.rpm 2>/dev/null || ls -lh dist/out/*.rpm
    exit 0
fi

# Native build
if [ -z "$VERSION" ]; then
    GIT_DESC=$(git describe --tags 2>/dev/null || echo "v0.1.0")
    BASE_VER=$(echo "$GIT_DESC" | cut -d '-' -f 1 | sed 's/^v//')
    MINOR=$(echo "$GIT_DESC" | cut -d '-' -f 2 -s)
    VERSION="${BASE_VER}${MINOR:+.$MINOR}"
fi
echo "Building dmart-${VERSION} RPM..."

# Step 1: Build binaries
echo "=== Building binaries ==="
./build.sh

# Step 2: Assemble source tarball for rpmbuild
STAGING=$(mktemp -d)
TARDIR="$STAGING/dmart-${VERSION}"
mkdir -p "$TARDIR/plugins"

# Binary (version info baked in via InformationalVersion)
cp bin/dmart "$TARDIR/"

# Plugin configs
cp -r plugins/*/ "$TARDIR/plugins/"

# Config sample
cp config.env.sample "$TARDIR/"

# Systemd unit + shell completions
cp dist/dmart.service "$TARDIR/"
cp dist/dmart.bash "$TARDIR/"
cp dist/dmart.fish "$TARDIR/"

# Create tarball
tar -czf "$STAGING/dmart-${VERSION}.tar.gz" -C "$STAGING" "dmart-${VERSION}"

# Step 3: Set up rpmbuild tree
RPMBUILD=$(mktemp -d)
mkdir -p "$RPMBUILD"/{SOURCES,SPECS,BUILD,RPMS,SRPMS}

cp "$STAGING/dmart-${VERSION}.tar.gz" "$RPMBUILD/SOURCES/"
# Bake the version into the spec so SRPM rebuilds work without --define
sed "s/^Version:.*$/Version:        ${VERSION}/" dist/dmart.spec > "$RPMBUILD/SPECS/dmart.spec"

# Step 4: Build binary RPM (use -bb, not -ba — tarball has pre-built binary, not source)
rpmbuild -bb \
    --define "_topdir $RPMBUILD" \
    "$RPMBUILD/SPECS/dmart.spec"

# Step 5: Copy output.
# Keep only one RPM per dist tag (fc44, el9, …) so dist/out/ ends up with
# at most three files: one binary RPM per target + one SRPM. Extract the
# tag from the filename (`dmart-VER-REL.<dist>.<arch>.rpm`, field NF-2) and
# purge every prior RPM sharing that tag before copying the fresh build in.
mkdir -p "$SRCDIR/dist/out"
for new_rpm in "$RPMBUILD"/RPMS/*/*.rpm; do
    [ -e "$new_rpm" ] || continue
    dist_tag=$(basename "$new_rpm" | awk -F. '{print $(NF-2)}')
    find "$SRCDIR/dist/out" -maxdepth 1 -type f \
        -name "*.${dist_tag}.*.rpm" ! -name "*.src.rpm" -delete
    cp "$new_rpm" "$SRCDIR/dist/out/"
done
if ls "$RPMBUILD"/SRPMS/*.src.rpm >/dev/null 2>&1; then
    rm -f "$SRCDIR/dist/out/"*.src.rpm
    cp "$RPMBUILD"/SRPMS/*.src.rpm "$SRCDIR/dist/out/"
fi

echo ""
echo "=== RPMs built successfully ==="
ls -lh "$SRCDIR/dist/out/"*.rpm

# Cleanup
rm -rf "$STAGING" "$RPMBUILD"
