namespace Dmart.DataAdapters.Sql;

// PostgreSQL DDL that mirrors dmart/backend/data_adapters/sql/create_tables.py.
//
// Key fidelity points:
//   * `tags`, `roles`, `groups`, `permissions`, `acl`, `relationships`, `subpaths`,
//     `resource_types`, `actions`, `conditions`, `languages`, `mirrors`,
//     `hide_folders`, `active_plugins`, `restricted_fields`, `allowed_fields_values`
//     are JSONB — not PostgreSQL ARRAY. Only `query_policies` is TEXT[].
//   * `displayname` and `description` are JSONB carrying Translation objects.
//   * `payload` is JSONB carrying a Payload object.
//   * `OTP.value` uses HSTORE — requires the `hstore` extension.
//   * `users.type` and `users.language` use PostgreSQL ENUM types `usertype` and
//     `language`. We pre-create them so dmart Python can hot-swap.
//   * Histories, Locks, Sessions, URLShorts, OTP, UserPermissionsCache
//     do NOT inherit from Metas — they're flat tables.
public static class SqlSchema
{
    public const string CreateAll = """
    -- Disable session-level statement / lock timeouts for the duration of
    -- this script. The TIMESTAMPTZ → TIMESTAMP migration below rewrites
    -- every row in tables like `entries` / `histories`, which can take
    -- minutes on production-sized databases. Without this, a per-user
    -- `statement_timeout` configured by an operator would abort the rewrite
    -- mid-flight and leave columns half-converted.
    SET statement_timeout = 0;
    SET lock_timeout = 0;

    CREATE EXTENSION IF NOT EXISTS hstore;
    CREATE EXTENSION IF NOT EXISTS pgcrypto;
    -- pg_trgm: trigram GIN acceleration for `@payload.body.x:*foo*` wildcard
    -- searches. The matching index is built CONCURRENTLY post-CreateAll
    -- (see SchemaInitializer + SqlSchema.ConcurrentIndexes) because
    -- CREATE INDEX CONCURRENTLY cannot run inside the implicit
    -- transaction that wraps this multi-statement simple-query.
    CREATE EXTENSION IF NOT EXISTS pg_trgm;

    DO $$ BEGIN
        CREATE TYPE language AS ENUM ('ar','en','ku','fr','tr');
    EXCEPTION WHEN duplicate_object THEN null; END $$;

    DO $$ BEGIN
        CREATE TYPE usertype AS ENUM ('web','mobile','bot');
    EXCEPTION WHEN duplicate_object THEN null; END $$;

    -- ============================================================
    -- USERS  (Metas + user-specific)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS users (
        uuid                    UUID PRIMARY KEY,
        shortname               TEXT NOT NULL UNIQUE,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               BOOLEAN NOT NULL DEFAULT FALSE,
        slug                    TEXT,
        displayname             JSONB,
        description             JSONB,
        tags                    JSONB,
        created_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname         TEXT NOT NULL,
        owner_group_shortname   TEXT,
        payload                 JSONB,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'user',

        password                TEXT,
        roles                   JSONB,
        groups                  JSONB,
        acl                     JSONB,
        relationships           JSONB,
        type                    usertype NOT NULL DEFAULT 'web',
        language                language NOT NULL DEFAULT 'en',
        email                   TEXT,
        msisdn                  TEXT,
        locked_to_device        BOOLEAN NOT NULL DEFAULT FALSE,
        is_email_verified       BOOLEAN NOT NULL DEFAULT FALSE,
        is_msisdn_verified      BOOLEAN NOT NULL DEFAULT FALSE,
        force_password_change   BOOLEAN NOT NULL DEFAULT TRUE,
        device_id               TEXT,
        google_id               TEXT,
        facebook_id             TEXT,
        apple_id                TEXT,
        social_avatar_url       TEXT,
        attempt_count           INTEGER,
        last_login              JSONB,
        notes                   TEXT,
        query_policies          TEXT[] NOT NULL DEFAULT '{}',

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- ROLES
    -- ============================================================
    CREATE TABLE IF NOT EXISTS roles (
        uuid                    UUID PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               BOOLEAN NOT NULL DEFAULT FALSE,
        slug                    TEXT,
        displayname             JSONB,
        description             JSONB,
        tags                    JSONB,
        created_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname         TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     JSONB,
        payload                 JSONB,
        relationships           JSONB,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'role',

        grantable_by            JSONB,
        permissions             JSONB,
        query_policies          TEXT[] NOT NULL DEFAULT '{}',

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- GROUPS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS groups (
        uuid                    UUID PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               BOOLEAN NOT NULL DEFAULT FALSE,
        slug                    TEXT,
        displayname             JSONB,
        description             JSONB,
        tags                    JSONB,
        created_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname         TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     JSONB,
        payload                 JSONB,
        relationships           JSONB,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'group',

        grantable_by            JSONB,
        query_policies          TEXT[] NOT NULL DEFAULT '{}',

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- PERMISSIONS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS permissions (
        uuid                    UUID PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               BOOLEAN NOT NULL DEFAULT FALSE,
        slug                    TEXT,
        displayname             JSONB,
        description             JSONB,
        tags                    JSONB,
        created_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname         TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     JSONB,
        payload                 JSONB,
        relationships           JSONB,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'permission',

        subpaths                JSONB,
        resource_types          JSONB,
        actions                 JSONB,
        conditions              JSONB,
        restricted_fields       JSONB,
        allowed_fields_values   JSONB,
        filter_fields_values    TEXT,
        query_policies          TEXT[] NOT NULL DEFAULT '{}',

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- ENTRIES (general content + tickets)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS entries (
        uuid                    UUID PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               BOOLEAN NOT NULL DEFAULT FALSE,
        slug                    TEXT,
        displayname             JSONB,
        description             JSONB,
        tags                    JSONB,
        created_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname         TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     JSONB,
        payload                 JSONB,
        relationships           JSONB,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL,

        state                   TEXT,
        is_open                 BOOLEAN,
        reporter                JSONB,
        workflow_shortname      TEXT,
        collaborators           JSONB,
        resolution_reason       TEXT,
        query_policies          TEXT[] NOT NULL DEFAULT '{}',

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- ATTACHMENTS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS attachments (
        uuid                    UUID PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               BOOLEAN NOT NULL DEFAULT FALSE,
        slug                    TEXT,
        displayname             JSONB,
        description             JSONB,
        tags                    JSONB,
        created_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname         TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     JSONB,
        payload                 JSONB,
        relationships           JSONB,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL,

        media                   BYTEA,
        body                    TEXT,
        state                   TEXT,

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- SPACES
    -- ============================================================
    CREATE TABLE IF NOT EXISTS spaces (
        uuid                            UUID PRIMARY KEY,
        shortname                       TEXT NOT NULL,
        space_name                      TEXT NOT NULL,
        subpath                         TEXT NOT NULL,
        is_active                       BOOLEAN NOT NULL DEFAULT FALSE,
        slug                            TEXT,
        displayname                     JSONB,
        description                     JSONB,
        tags                            JSONB,
        created_at                      TIMESTAMP NOT NULL DEFAULT NOW(),
        updated_at                      TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname                 TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname           TEXT,
        acl                             JSONB,
        payload                         JSONB,
        relationships                   JSONB,
        last_checksum_history           TEXT,
        resource_type                   TEXT NOT NULL DEFAULT 'space',

        root_registration_signature     TEXT NOT NULL DEFAULT '',
        primary_website                 TEXT NOT NULL DEFAULT '',
        indexing_enabled                BOOLEAN NOT NULL DEFAULT FALSE,
        capture_misses                  BOOLEAN NOT NULL DEFAULT FALSE,
        check_health                    BOOLEAN NOT NULL DEFAULT FALSE,
        languages                       JSONB,
        icon                            TEXT NOT NULL DEFAULT '',
        mirrors                         JSONB,
        hide_folders                    JSONB,
        hide_space                      BOOLEAN,
        active_plugins                  JSONB,
        ordinal                         INTEGER,
        query_policies                  TEXT[] NOT NULL DEFAULT '{}',

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- HISTORIES   (standalone — does NOT inherit from Metas)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS histories (
        uuid                  UUID PRIMARY KEY,
        request_headers       JSONB,
        diff                  JSONB,
        timestamp             TIMESTAMP NOT NULL DEFAULT NOW(),
        owner_shortname       TEXT,
        last_checksum_history TEXT,
        space_name            TEXT NOT NULL,
        subpath               TEXT NOT NULL,
        shortname             TEXT NOT NULL
    );

    -- ============================================================
    -- LOCKS  (Unique base only)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS locks (
        uuid              UUID PRIMARY KEY,
        shortname         TEXT NOT NULL,
        space_name        TEXT NOT NULL,
        subpath           TEXT NOT NULL,
        owner_shortname   TEXT NOT NULL,
        timestamp         TIMESTAMP NOT NULL DEFAULT NOW(),
        payload           JSONB,
        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- SESSIONS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS sessions (
        uuid             UUID PRIMARY KEY,
        shortname        TEXT NOT NULL,
        token            TEXT NOT NULL,
        timestamp        TIMESTAMP NOT NULL DEFAULT NOW(),
        firebase_token   TEXT
    );

    -- ============================================================
    -- URL SHORTS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS urlshorts (
        uuid        UUID PRIMARY KEY,
        token_uuid  TEXT NOT NULL,
        url         TEXT NOT NULL,
        timestamp   TIMESTAMP NOT NULL DEFAULT NOW()
    );

    -- ============================================================
    -- OTP  (uses hstore)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS otp (
        key       TEXT PRIMARY KEY,
        value     hstore NOT NULL,
        timestamp TIMESTAMP NOT NULL DEFAULT NOW()
    );

    -- ============================================================
    -- USERPERMISSIONSCACHE  (resolved permissions per user)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS userpermissionscache (
        user_shortname VARCHAR(64) PRIMARY KEY,
        permissions    JSONB
    );

    -- ============================================================
    -- PERFORMANCE INDEXES (mirrors create_tables.py)
    -- ============================================================
    CREATE INDEX IF NOT EXISTS idx_entries_space_name        ON entries (space_name);
    CREATE INDEX IF NOT EXISTS idx_entries_subpath           ON entries (subpath);
    CREATE INDEX IF NOT EXISTS idx_entries_owner_shortname   ON entries (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_entries_resource_type     ON entries (resource_type);
    CREATE INDEX IF NOT EXISTS idx_attachments_space_name    ON attachments (space_name);
    CREATE INDEX IF NOT EXISTS idx_attachments_subpath       ON attachments (subpath);
    CREATE INDEX IF NOT EXISTS idx_attachments_owner_shortname ON attachments (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_users_owner_shortname     ON users (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_roles_owner_shortname     ON roles (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_permissions_owner_shortname ON permissions (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_sessions_shortname        ON sessions (shortname);
    CREATE INDEX IF NOT EXISTS idx_histories_lookup
        ON histories (space_name, subpath, shortname, timestamp DESC);

    CREATE INDEX IF NOT EXISTS idx_entries_payload_gin
        ON entries USING GIN (payload jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_entries_tags_gin
        ON entries USING GIN (tags jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_entries_acl_gin
        ON entries USING GIN (acl jsonb_path_ops);
    -- Reverse referential-integrity probe in EntryService.DeleteAsync uses
    -- `relationships @> $1::jsonb`. Without this index it's a sequential scan
    -- over every entry; with it, jsonb_path_ops degrades the lookup to an
    -- index range scan and the gate stays cheap on large entry tables.
    CREATE INDEX IF NOT EXISTS idx_entries_relationships_gin
        ON entries USING GIN (relationships jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_users_roles_gin
        ON users USING GIN (roles jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_users_groups_gin
        ON users USING GIN (groups jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_roles_permissions_gin
        ON roles USING GIN (permissions jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_entries_schema_shortname
        ON entries ((payload->>'schema_shortname'));
    -- Slug is the canonical short identifier for entries (used by
    -- /public/query @slug:<value> and /entry/byslug). Without a btree
    -- here, a public slug lookup degrades to a sequential scan over the
    -- entries narrowed only by space_name+subpath+resource_type.
    CREATE INDEX IF NOT EXISTS idx_entries_slug ON entries (slug);

    CREATE INDEX IF NOT EXISTS idx_entries_query_policies_gin       ON entries USING GIN (query_policies);
    CREATE INDEX IF NOT EXISTS idx_users_query_policies_gin         ON users USING GIN (query_policies);
    CREATE INDEX IF NOT EXISTS idx_roles_query_policies_gin         ON roles USING GIN (query_policies);
    CREATE INDEX IF NOT EXISTS idx_permissions_query_policies_gin   ON permissions USING GIN (query_policies);
    CREATE INDEX IF NOT EXISTS idx_spaces_query_policies_gin        ON spaces USING GIN (query_policies);

    -- ============================================================
    -- FORWARD-COMPAT COLUMN PATCHES
    -- ------------------------------------------------------------
    -- dmart's Python port was developed with Alembic migrations, and existing
    -- deployments may have been created at an older revision that predates some
    -- columns we now reference in SELECTs/INSERTs. `CREATE TABLE IF NOT EXISTS`
    -- won't add missing columns on an already-existing table, so we patch
    -- individual columns here with `ADD COLUMN IF NOT EXISTS`. This is the C#
    -- equivalent of running Alembic's upgrade head — safe to re-run, no effect
    -- when the column already exists. Add new columns to this list when you
    -- extend a SQL SELECT/INSERT so older DBs get patched automatically.

    -- Security: the invitation feature was removed (it minted single-use
    -- login tokens and returned them in API responses — an account-takeover
    -- vector). Drop the table on existing deployments so any outstanding
    -- tokens are purged on upgrade. Idempotent.
    DROP TABLE IF EXISTS invitations;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS device_id             TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS google_id             TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS facebook_id           TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS apple_id              TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS social_avatar_url     TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS attempt_count         INTEGER;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS last_login            JSONB;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS notes                 TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS locked_to_device      BOOLEAN NOT NULL DEFAULT FALSE;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS query_policies        TEXT[] NOT NULL DEFAULT '{}';
    ALTER TABLE roles       ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;
    ALTER TABLE roles       ADD COLUMN IF NOT EXISTS grantable_by          JSONB;
    ALTER TABLE roles       ADD COLUMN IF NOT EXISTS query_policies        TEXT[] NOT NULL DEFAULT '{}';
    ALTER TABLE permissions ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;
    ALTER TABLE permissions ADD COLUMN IF NOT EXISTS query_policies        TEXT[] NOT NULL DEFAULT '{}';
    ALTER TABLE entries     ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;
    ALTER TABLE entries     ADD COLUMN IF NOT EXISTS query_policies        TEXT[] NOT NULL DEFAULT '{}';
    ALTER TABLE spaces      ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;
    ALTER TABLE spaces      ADD COLUMN IF NOT EXISTS query_policies        TEXT[] NOT NULL DEFAULT '{}';
    ALTER TABLE spaces      ADD COLUMN IF NOT EXISTS active_plugins        JSONB;
    ALTER TABLE spaces      ADD COLUMN IF NOT EXISTS hide_folders          JSONB;
    ALTER TABLE spaces      ADD COLUMN IF NOT EXISTS hide_space            BOOLEAN;
    ALTER TABLE spaces      ADD COLUMN IF NOT EXISTS ordinal               INTEGER;
    ALTER TABLE spaces      ADD COLUMN IF NOT EXISTS mirrors               JSONB;

    -- Identifier uniqueness on users — provider-issued IDs only.
    -- NULLs are distinct under Postgres' default, so multiple rows missing
    -- a given identifier coexist.
    --
    -- Email and msisdn are DELIBERATELY NOT indexed here. Two accounts with
    -- the same email must be able to coexist — e.g. a local password account
    -- and a Google OAuth account that happen to share an email. See
    -- OAuthEndpointsTests.Resolver_EmailMatch_CreatesSeparateAccount_NoSilentMerge,
    -- which pins the security property that the OAuth resolver creates a
    -- SEPARATE account rather than silently attaching its provider id to a
    -- pre-existing email-matching account. Adding a unique-email constraint
    -- would either break that test or force the resolver into a
    -- pre-auth-takeover-shaped merge path. Same reasoning applies to msisdn.
    -- Application-level uniqueness on /user/create still runs through
    -- UniquenessValidator.
    --
    -- Provider IDs (google_id, facebook_id, apple_id) are 1:1 by construction
    -- (one provider account → one provider-id-keyed dmart account, named
    -- `<provider>_<id>`), so DB-level uniqueness here is defense-in-depth on
    -- top of the shortname unique constraint.
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_google_id_unique
        ON users (google_id) WHERE google_id IS NOT NULL;
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_facebook_id_unique
        ON users (facebook_id) WHERE facebook_id IS NOT NULL;
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_apple_id_unique
        ON users (apple_id) WHERE apple_id IS NOT NULL;

    -- <table>.query_policies must be non-empty for every ACL-filterable
    -- table. A row with an empty array is invisible to AppendAclFilter
    -- (the row-level ACL intersects the caller's policy list against the
    -- row array via LIKE; empty never matches). Each repository's
    -- UpsertAsync regenerates policies on every write via
    -- QueryPolicies.Generate, so new writes satisfy the constraint
    -- automatically. The CHECK turns a silent data bug (invisible rows)
    -- into a loud DB error for any future write path that forgets to
    -- populate query_policies.
    --
    -- Skipped when orphan rows exist so the migration is safe on DBs that
    -- haven't been backfilled. Run `dmart fix_query_policies` first to
    -- heal orphans (the command covers all five tables), then re-run
    -- migrate (or restart dmart) to pick up the constraints.
    DO $$
    DECLARE
        t_name   TEXT;
        c_name   TEXT;
        orphans  INTEGER;
    BEGIN
        FOR t_name IN SELECT unnest(ARRAY['entries','users','roles','permissions','spaces'])
        LOOP
            c_name := t_name || '_query_policies_nonempty';
            CONTINUE WHEN EXISTS (SELECT 1 FROM pg_constraint WHERE conname = c_name);

            EXECUTE format(
                'SELECT COUNT(*) FROM %I WHERE COALESCE(array_length(query_policies, 1), 0) = 0',
                t_name
            ) INTO orphans;

            IF orphans > 0 THEN
                RAISE NOTICE 'Skipping CHECK %: % orphan row(s) in %. Run `dmart fix_query_policies` first.',
                    c_name, orphans, t_name;
                CONTINUE;
            END IF;

            EXECUTE format(
                'ALTER TABLE %I ADD CONSTRAINT %I CHECK (COALESCE(array_length(query_policies, 1), 0) > 0)',
                t_name, c_name
            );
        END LOOP;
    END $$;

    -- pgvector integration for semantic search. We deliberately DON'T run
    -- `CREATE EXTENSION vector` here — that requires superuser, which dmart's
    -- DB user usually isn't. A DBA installs the extension once via
    --   psql -U postgres -d <dbname> -c "CREATE EXTENSION vector"
    -- We just add the column if the extension is already installed; on
    -- pg_extension miss we no-op and runtime code gates all semantic
    -- features behind EmbeddingProvider.IsEnabledAsync.
    DO $$ BEGIN
        IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') THEN
            ALTER TABLE entries ADD COLUMN IF NOT EXISTS embedding vector;
        END IF;
    END $$;

    -- Python parity: Python dmart maps `datetime` to TIMESTAMP WITHOUT TIME ZONE
    -- via SQLModel/SQLAlchemy. Older C# deployments used TIMESTAMPTZ which made
    -- the application reason about UTC instants and a Local-vs-UTC conversion
    -- in the JSON converter, producing wire values shifted from what psql
    -- shows. The directive is "no UTC anywhere", so this DO block converts any
    -- still-`timestamptz` column to `timestamp without time zone` while
    -- preserving the wall-clock the operator currently sees in psql:
    -- `col AT TIME ZONE current_setting('TIMEZONE')` projects the stored UTC
    -- instant to the session's local wall-clock and drops the offset label.
    -- Idempotent — already-converted columns are filtered out by the
    -- information_schema lookup, so a second `dmart migrate` is a no-op.
    DO $$
    DECLARE
        r RECORD;
        tz TEXT := current_setting('TIMEZONE');
    BEGIN
        FOR r IN
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND data_type = 'timestamp with time zone'
              AND table_name IN ('entries','users','roles','permissions','spaces',
                                 'attachments','histories','locks','sessions',
                                 'otps','short_links')
        LOOP
            EXECUTE format(
                'ALTER TABLE %I ALTER COLUMN %I TYPE timestamp WITHOUT TIME ZONE
                 USING (%I AT TIME ZONE %L)',
                r.table_name, r.column_name, r.column_name, tz);
        END LOOP;
    END $$;
    """;

    // Indexes that MUST be created with CREATE INDEX CONCURRENTLY, kept
    // out of CreateAll because that string runs as one multi-statement
    // simple query which Postgres wraps in an implicit transaction —
    // CONCURRENTLY cannot run inside any transaction block.
    //
    // SchemaInitializer iterates this list AFTER CreateAll completes,
    // running each entry as its own NpgsqlCommand so the implicit-
    // transaction trap doesn't fire. IF NOT EXISTS is idempotent: on
    // a fresh install the index builds in milliseconds (empty table);
    // on an upgrade against an existing 750GB-class entries table it
    // builds in 2-6 hours without blocking writes.
    //
    // Recovery for a partially-built (invalid) index from a previous
    // interrupted run: connect as superuser and `DROP INDEX
    // idx_entries_payload_trgm;`, then restart. IF NOT EXISTS prevents
    // re-builds when the index is already valid; without manual
    // intervention an invalid index stays invalid (queries don't use
    // it; system functions correctly but wildcards seq-scan).
    public static readonly IReadOnlyList<string> ConcurrentIndexes = new[]
    {
        // Trigram GIN on the textual JSONB representation. Accelerates
        // `payload::text ILIKE @pattern` lookups for patterns ≥3 chars.
        // The wildcard branch in SearchExpressionParser emits this as a
        // prefilter before the precise per-path ILIKE check, so the
        // index narrows the candidate set quickly and the per-path
        // filter removes any false positives (e.g. JSON-key matches).
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_entries_payload_trgm " +
            "ON entries USING GIN ((payload::text) gin_trgm_ops)",
    };
}
