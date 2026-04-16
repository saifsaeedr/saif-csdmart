#!/bin/bash

# Build and run dmart (C#) + PostgreSQL all-in-one Alpine container.
#
# Uses a persistent builder container so the .NET SDK + NuGet cache
# survive between builds (~2 min first time, ~30s thereafter).
#
# Prerequisites: podman (or docker)

set -e
cd "$(dirname "$0")/../.."

BUILDER="dmart-alpine-builder"
BUILDER_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0-alpine"
VERSION="$(git describe --tags 2>/dev/null || echo dev)"

# 0. Build CXB frontend locally (if not already built)
if [ ! -f cxb/dist/client/index.html ]; then
    echo "Building CXB frontend..."
    ./build-cxb.sh
fi

# 1. Ensure builder container exists
NEED_CREATE=true
if podman container exists "$BUILDER" 2>/dev/null; then
    podman start "$BUILDER" 2>/dev/null || true
    sleep 1
    if podman exec "$BUILDER" true 2>/dev/null; then
        echo "Reusing existing $BUILDER container..."
        NEED_CREATE=false
    else
        echo "Builder container dead, recreating..."
        podman rm -f "$BUILDER" 2>/dev/null || true
    fi
fi

if [ "$NEED_CREATE" = true ]; then
    echo "Creating $BUILDER container (first time — installs SDK + build deps)..."
    podman run -d \
        --name "$BUILDER" \
        --userns=keep-id \
        --network=host \
        -v "$(pwd):/src:Z" \
        -w /src \
        "$BUILDER_IMAGE" \
        tail -f /dev/null
    podman exec --user root "$BUILDER" apk add --no-cache clang build-base zlib-dev
fi

# 2. Build AOT binary for musl (Alpine)
echo "Building dmart AOT binary (linux-musl-x64)..."
podman exec -e HOME=/tmp -w /src "$BUILDER" \
    dotnet publish dmart.csproj -r linux-musl-x64 \
        -p:PublishAot=true -p:StripSymbols=true -c Release \
        -p:InformationalVersion="$VERSION" \
        -o /src/bin/musl-out

# Copy binary to staging location (.dockerignore excludes bin/)
cp bin/musl-out/dmart admin_scripts/docker/dmart-binary
podman stop -t 1 "$BUILDER"

# 3. Remove old runtime container/image
podman rm -f dmart 2>/dev/null || true
podman rmi dmart 2>/dev/null || true

# 4. Build runtime image (no SDK needed — just copies the pre-built binary)
podman build \
    -t dmart \
    -f admin_scripts/docker/Dockerfile.runtime \
    --build-arg VERSION="$VERSION" \
    .
rm -f admin_scripts/docker/dmart-binary

# 5. Run the container
podman run --name dmart -p 8000:8000 -d dmart

echo ""
echo "=== dmart container started ==="
echo "  Web UI:  http://localhost:8000/cxb/"
echo "  API:     http://localhost:8000/"
echo ""
echo "Set admin password:"
echo "  podman exec -it dmart dmart set_password"
echo ""
echo "Check version:"
echo "  podman exec -it dmart dmart -v"
echo ""
echo "View logs:"
echo "  podman logs dmart"
echo "  podman logs -f dmart   # follow"
