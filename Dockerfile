# Stage 1: Build CXB frontend
FROM node:22-alpine AS cxb-build
RUN apk add --no-cache git
WORKDIR /cxb
COPY cxb/package.json cxb/yarn.lock* cxb/package-lock.json* ./
RUN yarn install || npm install
COPY cxb/ .
# Vite/Routify plugins run git commands during build; init a stub repo
# so they don't fail (the .git dir isn't copied into the container).
RUN git init && git config user.email "build@docker" && git config user.name "build" && git add -A && git commit -m "docker build" --allow-empty
RUN yarn build || npm run build

# Stage 2: Build C# AOT binary with embedded CXB
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src
COPY . .
# Copy the freshly-built CXB dist into cxb/dist/client/ so the
# EmbeddedResource glob in dmart.csproj picks it up.
COPY --from=cxb-build /cxb/dist/client/ cxb/dist/client/
RUN dotnet publish dmart.csproj -r linux-musl-x64 -p:PublishAot=true -c Release -o /out

# Stage 3: Minimal runtime image
FROM alpine:latest
RUN apk add --no-cache icu-libs krb5-libs
COPY --from=build /out /app
# CXB files served from filesystem (ManifestEmbeddedFileProvider doesn't
# work in native AOT on musl/Alpine). The middleware falls back to this.
COPY --from=cxb-build /cxb/dist/client/ /app/cxb/
WORKDIR /app
# Make all files world-readable so rootless Podman with UID remapping works.
RUN chmod -R a+rX /app
ENTRYPOINT ["./dmart"]
