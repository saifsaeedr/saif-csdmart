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
//   * Histories, Locks, Sessions, Invitations, URLShorts, OTP, UserPermissionsCache
//     do NOT inherit from Metas — they're flat tables.
public static class SqlSchema
{
    public const string CreateAll = """
    CREATE EXTENSION IF NOT EXISTS hstore;
    CREATE EXTENSION IF NOT EXISTS pgcrypto;

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
        created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
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
        created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        owner_shortname         TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     JSONB,
        payload                 JSONB,
        relationships           JSONB,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'role',

        permissions             JSONB,
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
        created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
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
        created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
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
        created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
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
        created_at                      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at                      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
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
        timestamp             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
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
        timestamp         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
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
        timestamp        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        firebase_token   TEXT
    );

    -- ============================================================
    -- INVITATIONS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS invitations (
        uuid              UUID PRIMARY KEY,
        invitation_token  TEXT NOT NULL UNIQUE,
        invitation_value  TEXT NOT NULL,
        timestamp         TIMESTAMPTZ NOT NULL DEFAULT NOW()
    );

    -- ============================================================
    -- URL SHORTS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS urlshorts (
        uuid        UUID PRIMARY KEY,
        token_uuid  TEXT NOT NULL,
        url         TEXT NOT NULL,
        timestamp   TIMESTAMPTZ NOT NULL DEFAULT NOW()
    );

    -- ============================================================
    -- OTP  (uses hstore)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS otp (
        key       TEXT PRIMARY KEY,
        value     hstore NOT NULL,
        timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW()
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
    CREATE INDEX IF NOT EXISTS idx_users_roles_gin
        ON users USING GIN (roles jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_users_groups_gin
        ON users USING GIN (groups jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_roles_permissions_gin
        ON roles USING GIN (permissions jsonb_path_ops);
    CREATE INDEX IF NOT EXISTS idx_entries_schema_shortname
        ON entries ((payload->>'schema_shortname'));

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
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS device_id             TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS google_id             TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS facebook_id           TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS social_avatar_url     TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS attempt_count         INTEGER;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS last_login            JSONB;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS notes                 TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS locked_to_device      BOOLEAN NOT NULL DEFAULT FALSE;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;
    ALTER TABLE users       ADD COLUMN IF NOT EXISTS query_policies        TEXT[] NOT NULL DEFAULT '{}';
    ALTER TABLE roles       ADD COLUMN IF NOT EXISTS last_checksum_history TEXT;
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

    -- Ensure invitations.invitation_token is UNIQUE even on DBs created
    -- before the constraint was added to the CREATE TABLE above.
    DO $$ BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'invitations_invitation_token_key'
        ) THEN
            ALTER TABLE invitations ADD CONSTRAINT invitations_invitation_token_key
                UNIQUE (invitation_token);
        END IF;
    END $$;

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
    """;
}
