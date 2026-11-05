-- Ejecutar conectado a bot_db, con un rol autorizado a crear objetos en public.
-- Incremental para un esquema compatible con init_bot_db.sql.
-- No actualiza datos existentes ni usa el historial de migraciones.
-- IF NOT EXISTS no corrige tablas existentes con estructura incompatible.
-- La API y el Worker necesitan SELECT, INSERT y UPDATE sobre estas tres tablas.
-- Si se crea con otro propietario, un administrador debe concederlos al rol
-- configurado en ConnectionStrings__DefaultConnection; sustituya el marcador:
-- GRANT USAGE ON SCHEMA public TO "ROL_APLICACION";
-- GRANT SELECT, INSERT, UPDATE ON public.execution_ownership,
--     public.remote_deliveries, public.delivery_agents TO "ROL_APLICACION";
-- Repita el GRANT para cada rol de aplicación si API y Worker usan roles distintos.
-- Los permisos de las tablas existentes deben conservarse y comprobarse aparte.
BEGIN;
SET LOCAL search_path TO public;

CREATE TABLE IF NOT EXISTS execution_ownership (
    job_id text PRIMARY KEY,
    worker_id text,
    lease_id uuid,
    fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
    lease_expires_at timestamptz,
    coordination_state text NOT NULL DEFAULT 'released' CHECK (coordination_state IN ('leased','released')),
    attempts integer NOT NULL DEFAULT 0,
    acquired_at timestamptz,
    renewed_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (coordination_state <> 'leased' OR (worker_id IS NOT NULL AND lease_id IS NOT NULL AND lease_expires_at IS NOT NULL))
);

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

COMMIT;
