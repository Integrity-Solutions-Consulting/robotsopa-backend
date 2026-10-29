-- Área de ensayo: no es todavía la persistencia activa de la aplicación.
BEGIN;
CREATE TABLE IF NOT EXISTS sri_schema_versions (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS sri_legacy_imports (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    imported_at timestamptz NOT NULL DEFAULT now(),
    state jsonb NOT NULL,
    sqlite_snapshot jsonb NOT NULL
);
INSERT INTO sri_schema_versions(version) VALUES (1) ON CONFLICT DO NOTHING;
COMMIT;
