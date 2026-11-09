-- SOPA: ejecutar manualmente en CloudBeaver sobre la base existente.
-- Seleccione el esquema de SOPA en search_path. Requiere panel_users(username).
-- No elimina ni modifica tablas o registros existentes; crea la tabla e índices si faltan.
-- IF NOT EXISTS no corrige una tabla preexistente incompatible; compruebe el resultado.
-- Conceda SELECT, INSERT y UPDATE sobre document_zip_jobs al rol usado por la API
-- mediante sus procedimientos habituales; este script no modifica roles ni permisos.
-- Non-destructive storage for ZIP preparation state. Archive bytes remain outside PostgreSQL.
BEGIN;
CREATE TABLE IF NOT EXISTS document_zip_jobs (
    id uuid PRIMARY KEY,
    username text NOT NULL REFERENCES panel_users(username),
    account_id text NOT NULL,
    selection jsonb NOT NULL,
    signature text NOT NULL,
    status text NOT NULL CHECK (status IN ('Pending','Preparing','Ready','Incomplete','Failed','Expired')),
    expected integer NOT NULL DEFAULT 0 CHECK (expected >= 0),
    processed integer NOT NULL DEFAULT 0 CHECK (processed >= 0),
    omitted integer NOT NULL DEFAULT 0 CHECK (omitted >= 0),
    issues jsonb NOT NULL DEFAULT '[]'::jsonb,
    warnings jsonb NOT NULL DEFAULT '[]'::jsonb,
    archive_name text,
    archive_sha256 text,
    archive_size bigint,
    instance_id uuid,
    lease_id uuid,
    lease_generation bigint NOT NULL DEFAULT 0,
    lease_expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS document_zip_jobs_recovery_idx ON document_zip_jobs(status, lease_expires_at, expires_at);
CREATE INDEX IF NOT EXISTS document_zip_jobs_owner_idx ON document_zip_jobs(username, account_id, created_at DESC);
COMMIT;
