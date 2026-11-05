-- A per-agent credential must be independently revocable and tied to one user.
-- Migration 005 has no place to store agent identities or credential hashes.
BEGIN;
CREATE TABLE IF NOT EXISTS delivery_agents (
    id uuid PRIMARY KEY,
    username text NOT NULL REFERENCES panel_users(username),
    label text NOT NULL,
    secret_hash bytea NOT NULL CHECK (octet_length(secret_hash) = 32),
    created_at timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz,
    last_seen_at timestamptz
);
CREATE INDEX IF NOT EXISTS delivery_agents_username_idx ON delivery_agents(username);
INSERT INTO sri_schema_migrations(version) VALUES (6) ON CONFLICT (version) DO NOTHING;
COMMIT;
