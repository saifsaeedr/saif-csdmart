# All-in-one Alpine container: dmart (AOT) + PostgreSQL
#
# Prerequisites: build UI first: ./build-ui.sh
#
# Usage:
#   podman build -t dmart -f admin_scripts/docker/Dockerfile .
#   podman run --name dmart -p 8000:8000 -d dmart

ARG VERSION=unknown

# Stage 1: Build C# AOT binary (musl for Alpine)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src

# Restore NuGet packages in a cacheable layer of their own — only the
# project files are needed for restore, so unrelated source changes don't
# invalidate the cache. dmart.csproj ProjectReferences Dmart.Models, so
# both csproj files must be present when the restore graph is computed.
COPY dmart.csproj ./
COPY Dmart.Models/Dmart.Models.csproj Dmart.Models/
RUN dotnet restore dmart.csproj -r linux-musl-x64

# Bring in the full source and publish. .dockerignore filters out
# dmart.Tests/, cxb/ source, bin/, obj/, .git/, and other bulk that
# shouldn't inflate the build layer.
COPY . .
RUN dotnet publish dmart.csproj -r linux-musl-x64 --no-restore \
        -p:PublishAot=true -p:StripSymbols=true -c Release -o /out \
    && rm -f /out/*.dbg /out/*.pdb /out/*.Development.json \
             /out/*.staticwebassets* /out/*.deps.json

# Stage 2: Minimal runtime
FROM alpine:edge

# Install packages + copy all files in one layer
RUN apk add --no-cache \
      postgresql18 postgresql18-contrib \
      bash curl tzdata icu-libs krb5-libs

COPY --from=build --chmod=755 /out/dmart /usr/bin/dmart
COPY --from=build /out/plugins/ /usr/lib/dmart/plugins/
# Filesystem fallback copies of the embedded SPAs — musl-AOT doesn't always
# surface ManifestEmbeddedFileProvider resources, so the middleware falls
# back to these dirs at runtime (see CxbMiddleware / CatalogMiddleware).
COPY cxb/dist/client/     /usr/lib/dmart/cxb/
COPY catalog/dist/client/ /usr/lib/dmart/catalog/
COPY --chmod=755 admin_scripts/docker/entrypoint.sh /entrypoint.sh

ENV DOTNET_SYSTEM_NET_SECURITY_DISABLEGSSAPIPAL=true
LABEL name="dmart" vendor="dmart.cc" version="${VERSION}" license="AGPLv3+" \
      org.opencontainers.image.name="dmart" \
      org.opencontainers.image.vendor="dmart.cc" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.description="dmart C# with PostgreSQL — all-in-one Alpine container" \
      org.opencontainers.image.url="https://dmart.cc" \
      org.opencontainers.image.source="https://github.com/edraj/csdmart" \
      org.opencontainers.image.license="AGPLv3+"
EXPOSE 8000
CMD ["/entrypoint.sh"]
