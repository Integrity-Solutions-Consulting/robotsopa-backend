BEGIN;
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
INSERT INTO sri_schema_migrations(version) VALUES (4) ON CONFLICT (version) DO NOTHING;
COMMIT;
