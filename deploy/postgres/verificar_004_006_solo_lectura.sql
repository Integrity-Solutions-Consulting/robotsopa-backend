-- Ejecutar únicamente contra la base existente, con un usuario de solo lectura.
-- Inspecciona estructura efectiva; no ejecuta migraciones ni modifica datos.
BEGIN READ ONLY;
SELECT current_database() AS base_actual, current_schema() AS esquema_actual;

WITH required(version, relation, column_name) AS (VALUES
 (4,'execution_ownership','job_id'),(4,'execution_ownership','worker_id'),(4,'execution_ownership','lease_id'),
 (4,'execution_ownership','fencing_token'),(4,'execution_ownership','lease_expires_at'),(4,'execution_ownership','coordination_state'),
 (4,'execution_ownership','attempts'),(4,'execution_ownership','acquired_at'),(4,'execution_ownership','renewed_at'),(4,'execution_ownership','updated_at'),
 (5,'remote_deliveries','id'),(5,'remote_deliveries','username'),(5,'remote_deliveries','account_id'),(5,'remote_deliveries','document_id'),
 (5,'remote_deliveries','kind'),(5,'remote_deliveries','source_path'),(5,'remote_deliveries','destination_path'),(5,'remote_deliveries','sha256'),
 (5,'remote_deliveries','status'),(5,'remote_deliveries','attempts'),(5,'remote_deliveries','last_error'),(5,'remote_deliveries','created_at'),(5,'remote_deliveries','delivered_at'),
 (6,'delivery_agents','id'),(6,'delivery_agents','username'),(6,'delivery_agents','label'),(6,'delivery_agents','secret_hash'),
 (6,'delivery_agents','created_at'),(6,'delivery_agents','revoked_at'),(6,'delivery_agents','last_seen_at')
)
SELECT r.version, r.relation, r.column_name,
       to_regclass(r.relation) IS NOT NULL AS tabla_existe,
       a.attname IS NOT NULL AS columna_existe,
       format_type(a.atttypid, a.atttypmod) AS tipo_actual,
       CASE
         WHEN a.attname IS NULL THEN false
         WHEN r.column_name IN ('id','lease_id') AND (r.relation <> 'execution_ownership' OR r.column_name = 'lease_id') THEN format_type(a.atttypid,a.atttypmod)='uuid'
         WHEN r.column_name='fencing_token' THEN format_type(a.atttypid,a.atttypmod)='bigint'
         WHEN r.column_name='attempts' THEN format_type(a.atttypid,a.atttypmod)='integer'
         WHEN r.column_name='secret_hash' THEN format_type(a.atttypid,a.atttypmod)='bytea'
         WHEN r.column_name IN ('lease_expires_at','acquired_at','renewed_at','updated_at','created_at','delivered_at','revoked_at','last_seen_at') THEN format_type(a.atttypid,a.atttypmod)='timestamp with time zone'
         ELSE format_type(a.atttypid,a.atttypmod)='text' OR format_type(a.atttypid,a.atttypmod) LIKE 'character varying%'
       END AS tipo_compatible
FROM required r
LEFT JOIN pg_attribute a ON a.attrelid=to_regclass(r.relation) AND a.attname=r.column_name AND a.attnum>0 AND NOT a.attisdropped
ORDER BY r.version,r.relation,r.column_name;

WITH required(relation, keys) AS (VALUES
 ('execution_ownership', ARRAY['job_id']::text[]),
 ('remote_deliveries', ARRAY['username','document_id','kind','sha256']::text[]),
 ('delivery_agents', ARRAY['id']::text[])
)
SELECT r.relation,r.keys,EXISTS (
 SELECT 1 FROM pg_index i WHERE i.indrelid=to_regclass(r.relation) AND i.indisunique AND i.indisvalid AND i.indimmediate
 AND i.indpred IS NULL AND i.indnkeyatts=cardinality(r.keys)
 AND ARRAY(SELECT pg_get_indexdef(i.indexrelid,n,true) FROM generate_series(1,i.indnkeyatts) n ORDER BY 1)
     = ARRAY(SELECT k FROM unnest(r.keys) k ORDER BY k)
) AS indice_unico_valido FROM required r;

SELECT tablename,indexname,indexdef FROM pg_indexes
WHERE schemaname=current_schema() AND tablename IN ('execution_ownership','remote_deliveries','delivery_agents')
ORDER BY tablename,indexname;
SELECT c.relname AS tabla, con.conname AS restriccion, con.contype AS tipo, pg_get_constraintdef(con.oid) AS definicion
FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid
WHERE c.oid IN (to_regclass('execution_ownership'),to_regclass('remote_deliveries'),to_regclass('delivery_agents'))
ORDER BY c.relname,con.conname;
COMMIT;
