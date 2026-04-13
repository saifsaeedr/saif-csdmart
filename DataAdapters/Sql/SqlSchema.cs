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
//   * Materialized views mv_user_roles and mv_role_permissions feed permission
//     resolution; recreated from the JSONB user.roles / role.permissions arrays.
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
    -- USER PERMISSIONS CACHE
    -- ============================================================
    CREATE TABLE IF NOT EXISTS user_permissions_cache (
        user_shortname  TEXT PRIMARY KEY,
        permissions     JSONB NOT NULL DEFAULT '{}'::jsonb
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
        invitation_token  TEXT NOT NULL,
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
    -- COUNT_HISTORY  (analytics — populated by both dmart Python and dmart C#)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS count_history (
        id            SERIAL PRIMARY KEY,
        spacename     VARCHAR(255) NOT NULL,
        entries_count BIGINT NOT NULL,
        recorded_at   TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
    );
    CREATE INDEX IF NOT EXISTS idx_count_history_spacename ON count_history (spacename);

    -- ============================================================
    -- ALEMBIC MIGRATION TRACKING
    -- ============================================================
    -- dmart Python uses Alembic. We mirror the same head version so dmart Python's
    -- alembic upgrade command sees the schema as already-current and doesn't try to
    -- re-apply migrations. The version below is dmart's current head as of this port;
    -- update it whenever dmart releases a new migration.
    CREATE TABLE IF NOT EXISTS alembic_version (
        version_num VARCHAR(32) NOT NULL,
        CONSTRAINT alembic_version_pkc PRIMARY KEY (version_num)
    );
    INSERT INTO alembic_version (version_num) VALUES ('b2c3d4e5f6a7')
    ON CONFLICT (version_num) DO NOTHING;

    -- ============================================================
    -- AUTHZ MV META + MATERIALIZED VIEWS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS authz_mv_meta (
        id              INT PRIMARY KEY,
        last_source_ts  TIMESTAMPTZ,
        refreshed_at    TIMESTAMPTZ
    );
    INSERT INTO authz_mv_meta (id, last_source_ts, refreshed_at)
    VALUES (1, to_timestamp(0), now())
    ON CONFLICT (id) DO NOTHING;

    CREATE MATERIALIZED VIEW IF NOT EXISTS mv_user_roles AS
    SELECT u.shortname AS user_shortname,
           r.shortname AS role_shortname
    FROM users u
    JOIN LATERAL jsonb_array_elements_text(u.roles) AS role_name ON TRUE
    JOIN roles r ON r.shortname = role_name;
    CREATE UNIQUE INDEX IF NOT EXISTS idx_mv_user_roles_unique
        ON mv_user_roles (user_shortname, role_shortname);

    CREATE MATERIALIZED VIEW IF NOT EXISTS mv_role_permissions AS
    SELECT r.shortname AS role_shortname,
           p.shortname AS permission_shortname
    FROM roles r
    JOIN LATERAL jsonb_array_elements_text(r.permissions) AS perm_name ON TRUE
    JOIN permissions p ON p.shortname = perm_name;
    CREATE UNIQUE INDEX IF NOT EXISTS idx_mv_role_permissions_unique
        ON mv_role_permissions (role_shortname, permission_shortname);

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
    """;
}
