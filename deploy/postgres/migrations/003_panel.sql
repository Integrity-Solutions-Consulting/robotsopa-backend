-- Schema for the panel; no application data is removed or rewritten.
BEGIN;
CREATE TABLE IF NOT EXISTS panel_users (
    username text PRIMARY KEY,
    role text NOT NULL CHECK (role IN ('admin', 'asistente')),
    salt bytea,
    password_hash bytea,
    must_change boolean NOT NULL DEFAULT false,
    CHECK ((salt IS NULL) = (password_hash IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS panel_users_username_ci ON panel_users (lower(username));
CREATE TABLE IF NOT EXISTS panel_accounts (
    id text PRIMARY KEY,
    name text NOT NULL,
    tax_id text,
    active boolean NOT NULL DEFAULT true,
    is_test boolean NOT NULL DEFAULT false
);
CREATE TABLE IF NOT EXISTS panel_assignments (
    username text NOT NULL REFERENCES panel_users(username),
    account_id text NOT NULL REFERENCES panel_accounts(id),
    PRIMARY KEY (username, account_id)
);
CREATE INDEX IF NOT EXISTS panel_assignments_account_idx ON panel_assignments (account_id);
CREATE TABLE IF NOT EXISTS panel_preferences (
    username text PRIMARY KEY REFERENCES panel_users(username),
    default_account text REFERENCES panel_accounts(id),
    download_subfolder text
);
CREATE TABLE IF NOT EXISTS panel_audit (
    id text PRIMARY KEY,
    username text NOT NULL,
    action text NOT NULL,
    occurred_at timestamptz NOT NULL,
    account_id text,
    result text NOT NULL,
    is_test boolean NOT NULL DEFAULT false,
    account_name text
);
CREATE INDEX IF NOT EXISTS panel_audit_occurred_idx ON panel_audit (occurred_at DESC);
INSERT INTO sri_schema_migrations(version) VALUES (3) ON CONFLICT (version) DO NOTHING;
COMMIT;
