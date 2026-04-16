#!/bin/sh
set -e

PGDATA="/var/lib/postgresql/18/data"
MARKER="/root/.dmart/.db_initialized"

# --- First run: initialize dmart config + PostgreSQL ---
if [ ! -f "$MARKER" ]; then
  echo "=== First run: initializing ==="

  # Generate dmart config
  dmart init
  cat > /root/.dmart/config.json << 'CONF'
{
  "title": "DMART Unified Data Platform",
  "footer": "dmart.cc unified data platform",
  "short_name": "dmart",
  "display_name": "dmart",
  "description": "dmart unified data platform",
  "default_language": "en",
  "languages": { "ar": "ا��عربية", "en": "English" },
  "backend": "http://localhost:8000",
  "websocket": "ws://localhost:8000/ws"
}
CONF

  # Generate random credentials
  PGPASS=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 16)
  JWT_SECRET=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 48)

  cat >> /root/.dmart/config.env << EOF
LISTENING_PORT=8000
ALLOWED_CORS_ORIGINS="http://localhost:8000"
DATABASE_NAME='dmart'
DATABASE_USERNAME='dmart'
DATABASE_PASSWORD='$PGPASS'
JWT_SECRET='$JWT_SECRET'
EOF

  # Initialize PostgreSQL
  mkdir -p /run/postgresql
  chown -R postgres:postgres /run/postgresql
  echo "$PGPASS" > /tmp/pgpass
  su - postgres -c "initdb --auth=scram-sha-256 -U dmart --pwfile=/tmp/pgpass $PGDATA"

  su - postgres -c "pg_ctl start -D $PGDATA -l /dev/null"
  PGPASSWORD="$PGPASS" createdb -h 127.0.0.1 -U dmart dmart
  su - postgres -c "pg_ctl stop -D $PGDATA -m fast"

  rm -f /tmp/pgpass
  touch "$MARKER"
  echo "=== Initialized ==="
fi

# --- Start PostgreSQL in background ---
mkdir -p /run/postgresql
chown postgres:postgres /run/postgresql
touch /var/log/postgresql.log && chown postgres:postgres /var/log/postgresql.log
su - postgres -c "pg_ctl start -D $PGDATA -l /var/log/postgresql.log"

# --- Graceful shutdown: stop dmart, then PG ---
shutdown() {
  [ -n "$DMART_PID" ] && kill -TERM "$DMART_PID" 2>/dev/null && wait "$DMART_PID" 2>/dev/null
  su - postgres -c "pg_ctl stop -D $PGDATA -m fast" 2>/dev/null
  exit 0
}
trap shutdown TERM INT

# --- Start dmart in background, wait for it ---
export BACKEND_ENV="/root/.dmart/config.env"
/usr/bin/dmart serve --cxb-config /root/.dmart/config.json &
DMART_PID=$!
wait "$DMART_PID"
