-- Transitional state storage. Apply using the controlled migration command.
BEGIN;
CREATE TABLE IF NOT EXISTS sri_schema_migrations (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS app_state (
    id smallint PRIMARY KEY CHECK (id = 1),
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object')
);
INSERT INTO app_state (id, payload) VALUES (1, '{}'::jsonb)
    ON CONFLICT (id) DO NOTHING;
INSERT INTO sri_schema_migrations (version) VALUES (2)
    ON CONFLICT (version) DO NOTHING;
COMMIT;
