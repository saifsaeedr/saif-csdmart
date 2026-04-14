# All-in-one Alpine container: dmart (AOT) + PostgreSQL + Caddy + OpenRC
#
# Prerequisites: build CXB first: ./build-cxb.sh
#
# Usage:
#   podman build -t dmart -f admin_scripts/docker/Dockerfile .
#   podman run --name dmart -p 8000:8000 -d -it dmart

ARG VERSION=unknown

# Stage 1: Build C# AOT binary (musl for Alpine)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src
COPY . .
RUN dotnet publish dmart.csproj -r linux-musl-x64 \
        -p:PublishAot=true -p:StripSymbols=true -c Release -o /out \
    && rm -f /out/*.dbg /out/*.pdb /out/*.Development.json \
             /out/*.staticwebassets* /out/*.deps.json

# Stage 2: All-in-one runtime with PostgreSQL + Caddy + OpenRC
FROM alpine:edge

RUN set -eux \
  # PostgreSQL
  && apk add postgresql18 postgresql18-contrib postgresql18-openrc \
  # Runtime deps + utilities
  && apk add openrc bash curl jq caddy tzdata coreutils icu-libs \
  # PostgreSQL init
  && mkdir -p /run/postgresql && chown -R postgres:postgres /run/postgresql \
  && tr -dc A-Za-z0-9 </dev/urandom | head -c 13 > /tmp/pgpass \
  && su - postgres -c 'initdb --auth=scram-sha-256 -U dmart --pwfile=/tmp/pgpass \
       /var/lib/postgresql/18/data' \
  # Start on a non-default port to avoid conflict with host PostgreSQL during build
  && su - postgres -c 'pg_ctl start -t 5000 -D /var/lib/postgresql/18/data -o "-p 15432"' \
  && export PGPASSWORD=$(cat /tmp/pgpass) \
  && createdb -h 127.0.0.1 -p 15432 -U dmart dmart \
  # Stop and reset to default port for runtime
  && su - postgres -c 'pg_ctl stop -D /var/lib/postgresql/18/data' \
  # System user
  && adduser -DH dmart \
  # OpenRC container fixes
  && sed -i 's/^\(tty\d\:\:\)/#\1/g' /etc/inittab \
  && sed -i \
     -e 's/#rc_sys=".*"/rc_sys="docker"/g' \
     -e 's/#rc_env_allow=".*"/rc_env_allow="\*"/g' \
     -e 's/#rc_crashed_stop=.*/rc_crashed_stop=NO/g' \
     -e 's/#rc_crashed_start=.*/rc_crashed_start=YES/g' \
     -e 's/#rc_provide=".*"/rc_provide="loopback net"/g' \
     /etc/rc.conf \
  && rm -f /etc/init.d/hwdrivers /etc/init.d/hwclock \
     /etc/init.d/modules /etc/init.d/modules-load \
     /etc/init.d/modloop /etc/init.d/machine-id \
  && > /usr/libexec/rc/sh/rc-cgroup.sh \
  && sed -i 's/VSERVER/DOCKER/Ig' /usr/libexec/rc/sh/init.sh

# Copy dmart binary + plugin configs
COPY --from=build --chmod=755 /out/dmart /usr/bin/dmart
COPY --from=build /out/plugins/ /usr/lib/dmart/plugins/

# CXB static files (pre-built locally via ./build-cxb.sh)
COPY cxb/dist/client/ /usr/lib/dmart/cxb/

# OpenRC service + Caddy config
COPY admin_scripts/docker/dmart.sysv /etc/init.d/dmart
COPY admin_scripts/docker/Caddyfile /etc/caddy/Caddyfile
RUN chmod +x /etc/init.d/dmart

# Initialize dmart config + register services
RUN set -eux \
  && dmart init \
  # Override config.json to point to Caddy's port (8000)
  && printf '{\n  "title": "DMART Unified Data Platform",\n  "footer": "dmart.cc unified data platform",\n  "short_name": "dmart",\n  "display_name": "dmart",\n  "description": "dmart unified data platform",\n  "default_language": "en",\n  "languages": { "ar": "العربية", "en": "English" },\n  "backend": "http://localhost:8000",\n  "websocket": "ws://localhost:8000/ws"\n}\n' > /root/.dmart/config.json \
  && echo "DATABASE_NAME='dmart'" >> /root/.dmart/config.env \
  && echo "DATABASE_USERNAME='dmart'" >> /root/.dmart/config.env \
  && echo "DATABASE_PASSWORD='$(cat /tmp/pgpass)'" >> /root/.dmart/config.env \
  && echo "LISTENING_PORT=8282" >> /root/.dmart/config.env \
  && echo 'ALLOWED_CORS_ORIGINS="http://localhost:8000"' >> /root/.dmart/config.env \
  && echo 'JWT_SECRET="'$(tr -dc A-Za-z0-9 </dev/urandom | head -c 48)'"' >> /root/.dmart/config.env \
  # Log directory
  && mkdir -p /home/logs \
  && touch /home/logs/caddy.log \
  && chmod -R g+xw /home/logs \
  && chown -R root:caddy /home/logs \
  # Cleanup
  && rm -f /tmp/pgpass \
  # Register services with OpenRC
  && rc-update add postgresql \
  && rc-update add dmart \
  && rc-update add caddy

ENV DOTNET_SYSTEM_NET_SECURITY_DISABLEGSSAPIPAL=true

LABEL name="dmart" \
      vendor="dmart.cc" \
      version="${VERSION}" \
      license="AGPLv3+" \
      org.opencontainers.image.name="dmart" \
      org.opencontainers.image.vendor="dmart.cc" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.description="dmart C# with PostgreSQL — all-in-one Alpine container" \
      org.opencontainers.image.url="https://dmart.cc" \
      org.opencontainers.image.source="https://github.com/edraj/csdmart" \
      org.opencontainers.image.license="AGPLv3+"

EXPOSE 8000

CMD ["/sbin/init"]
