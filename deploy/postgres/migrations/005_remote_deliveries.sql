BEGIN;
CREATE TABLE IF NOT EXISTS remote_deliveries (
    id uuid PRIMARY KEY,
    username text NOT NULL REFERENCES panel_users(username),
    account_id text NOT NULL REFERENCES panel_accounts(id),
    document_id text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('xml', 'pdf', 'xlsx')),
    source_path text NOT NULL,
    destination_path text NOT NULL,
    sha256 text NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Delivered')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    delivered_at timestamptz,
    UNIQUE (username, document_id, kind, sha256)
);
CREATE INDEX IF NOT EXISTS remote_deliveries_user_pending_idx ON remote_deliveries (username, created_at) WHERE status = 'Pending';
INSERT INTO sri_schema_migrations(version) VALUES (5) ON CONFLICT (version) DO NOTHING;
COMMIT;
