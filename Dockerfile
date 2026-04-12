# Stage 1: Build CXB frontend
FROM node:22-alpine AS cxb-build
WORKDIR /cxb
COPY cxb/package.json cxb/yarn.lock* cxb/package-lock.json* ./
RUN if [ -f yarn.lock ]; then yarn install --frozen-lockfile; \
    else npm ci; fi
COPY cxb/ .
RUN if [ -f yarn.lock ]; then yarn build; \
    else npm run build; fi

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
WORKDIR /app
ENTRYPOINT ["./dmart"]
