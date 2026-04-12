FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src
COPY . .
RUN dotnet publish dmart.csproj -r linux-musl-x64 -p:PublishAot=true -c Release -o /out

FROM alpine:latest
RUN apk add --no-cache icu-libs krb5-libs
COPY --from=build /out /app
WORKDIR /app
ENTRYPOINT ["./dmart"]
