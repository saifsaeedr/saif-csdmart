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
    # Build CXB locally (static files, no platform dependency). Fail the whole
    # RPM build if CXB fails — otherwise the RPM ships with a stale frontend.
    if [ -f cxb/package.json ] && [ ! -f cxb/dist/client/index.html ]; then
        echo "Building CXB frontend locally..."
        ./build-cxb.sh || { echo "CXB build failed" >&2; exit 1; }
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
            if $ENGINE exec "$CONTAINER_NAME" getent hosts api.nuget.org >/dev/null 2>&1; then
                echo "Reusing existing $CONTAINER_NAME container..."
                NEED_CREATE=false
            else
                echo "Container $CONTAINER_NAME has broken DNS, recreating..."
                $ENGINE rm -f "$CONTAINER_NAME" 2>/dev/null || true
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
        $ENGINE run -d \
            --name "$CONTAINER_NAME" \
            --userns=keep-id \
            --network=host \
            -v "${SRCDIR}:/src:Z" \
            -v "${HOST_NUGET_CACHE}:/nuget-packages:Z" \
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

# Step 5: Copy output
mkdir -p "$SRCDIR/dist/out"
cp "$RPMBUILD"/RPMS/*/*.rpm "$SRCDIR/dist/out/"
cp "$RPMBUILD"/SRPMS/*.src.rpm "$SRCDIR/dist/out/" 2>/dev/null || true

echo ""
echo "=== RPMs built successfully ==="
ls -lh "$SRCDIR/dist/out/"*.rpm

# Cleanup
rm -rf "$STAGING" "$RPMBUILD"
