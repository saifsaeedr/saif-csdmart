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
#   ./dist/build-rpm.sh fc44         # build for Fedora 44 (native, same as no arg)
#   VERSION=1.0.0 ./dist/build-rpm.sh el9   # explicit version

cd "$(dirname "$0")/.."
SRCDIR="$(pwd)"

TARGET="${1:-}"

# Build inside a container for cross-distro targets
if [[ "$TARGET" == "el9" || "$TARGET" == "rhel9" ]]; then
    ENGINE="${CONTAINER_ENGINE:-podman}"
    VERSION="${VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo "0.1.0")}"
    echo "Building dmart-${VERSION} RPM for RHEL 9 via ${ENGINE}..."
    mkdir -p dist/out
    $ENGINE run --rm \
        --network=host \
        -v "${SRCDIR}:/src:Z" \
        -w /src \
        -e VERSION="$VERSION" \
        almalinux:9 \
        bash -c '
            rpm -Uvh https://packages.microsoft.com/config/rhel/9/packages-microsoft-prod.rpm &&
            dnf module -y reset nodejs && dnf module -y enable nodejs:20 &&
            dnf install -y dotnet-sdk-10.0 rpm-build clang zlib-devel git nodejs npm --nobest &&
            npm install -g yarn &&
            ./dist/build-rpm.sh
        '
    echo ""
    echo "=== RHEL 9 RPM ==="
    ls -lh dist/out/*el9*.rpm 2>/dev/null || ls -lh dist/out/*.rpm
    exit 0
fi

# Native build
VERSION="${VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo "0.1.0")}"
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
cp dist/dmart.spec "$RPMBUILD/SPECS/"

# Step 4: Build RPM
rpmbuild -bb \
    --define "_topdir $RPMBUILD" \
    --define "version $VERSION" \
    "$RPMBUILD/SPECS/dmart.spec"

# Step 5: Copy output
mkdir -p "$SRCDIR/dist/out"
cp "$RPMBUILD"/RPMS/*/*.rpm "$SRCDIR/dist/out/"

echo ""
echo "=== RPM built successfully ==="
ls -lh "$SRCDIR/dist/out/"*.rpm

# Cleanup
rm -rf "$STAGING" "$RPMBUILD"
