#!/bin/bash -x

# Build and run dmart (C#) + PostgreSQL all-in-one Alpine container.
#
# Prerequisites: podman (or docker)

set -e
cd "$(dirname "$0")/../.."

ENGINE="${CONTAINER_ENGINE:-podman}"

# 0. Build CXB frontend locally (if not already built)
if [ ! -f cxb/dist/client/index.html ]; then
    echo "Building CXB frontend..."
    ./build-cxb.sh
fi

# 1. Remove old container/image
$ENGINE rm -f dmart 2>/dev/null || true
$ENGINE rmi dmart 2>/dev/null || true

# 2. Build container image (dotnet SDK → alpine runtime)
$ENGINE build \
    -t dmart \
    -f admin_scripts/docker/Dockerfile \
    --build-arg VERSION="$(git describe --tags 2>/dev/null || echo dev)" \
    .

# 3. Run the container
$ENGINE run --name dmart -p 8000:8000 -d -it dmart

echo ""
echo "=== dmart container started ==="
echo "  Web UI:  http://localhost:8000/cxb/"
echo "  API:     http://localhost:8000/"
echo ""
echo "Set admin password:"
echo "  $ENGINE exec -it dmart dmart set_password"
echo ""
echo "Check version:"
echo "  $ENGINE exec -it dmart dmart -v"
echo ""
echo "View logs:"
echo "  $ENGINE exec -it dmart cat /home/logs/dmart-out"
echo "  $ENGINE exec -it dmart cat /home/logs/dmart-err"
