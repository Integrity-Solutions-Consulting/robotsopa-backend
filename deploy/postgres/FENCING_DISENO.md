# Coordinación de ejecuciones: mapa y diseño pendiente

Este documento describe el estado observado y una propuesta. No indica que la cola distribuida esté implementada.

## Mapa de escrituras

| Origen | Escrituras | Tratamiento requerido |
| --- | --- | --- |
| API `Program.cs` | Alta, cancelación, reanudación y reintento de ejecuciones; programaciones, importación y edición de documentos. Al arrancar puede cambiar ejecuciones SRI. | Operaciones de usuario explícitas y autorizadas, transaccionales con la coordinación. El cambio de estados al arrancar debe revisarse: no puede invalidar trabajos con lease vigente. |
| API `AgentEndpoints.cs` y autenticación heredada | El agente puede comunicar estados y progreso de una ejecución. | No admitir informes asociados a un trabajo de producción sin vincularlos a la generación vigente; conservar acceso heredado solo con un contrato explícito. |
| `JobProcessor` | Adquisición, recuperación, programación, heartbeat, progreso, resultados, reintentos, cancelación y auditoría. | Adquirir y mutar bajo propiedad válida; no recuperar indiscriminadamente todos los `Running`. La programación debe ser atómica e idempotente. |
| `SriExecutionEngine` | Progreso, resultados por consulta, errores, checkpoints, documentos y contadores. | Propagar el contexto de ejecución explícitamente; cada mutación debe validarse en la misma transacción que escribe. |
| `SriPortal`, `ServerWorkerAuthentication` y `WorkerAuthentication` | Estados de autenticación, progreso y revocación de sesión. | Vincular los registros y mutaciones al contexto vigente; evitar que un Worker antiguo revoque una sesión usada por uno nuevo. |
| `DownloadPipeline` y `DocumentStorage` | Archivos, metadatos y creación de entregas SharePoint. | Validar propiedad transaccionalmente para metadatos; comprobarla antes del efecto externo. El archivo usa clave determinista y comparación de contenido, pero el filesystem no acepta fencing y no garantiza ejecución exactamente una vez. |
| `SharePointUploadProcessor` | Lease propio, subida, confirmación y borrado del archivo local. | Mantener coordinación de entrega separada y comprobar propiedad en la confirmación; la expiración durante una petición puede causar solicitudes repetidas. Reconciliar por ruta y hash antes de repetir o borrar. |
| `DocumentIndexer` y API de documentos | Indexación o cambios administrativos independientes de un trabajo. | Mantener autorización administrativa y definir conflictos con trabajos activos, sin exigir un lease ficticio. |

## Contrato propuesto

`ExecutionContext(JobId, WorkerId, LeaseId, FencingToken)` debe pasarse explícitamente a los componentes de ejecución; no usar una variable global como única frontera. La API usa operaciones administrativas diferenciadas. El repositorio debe verificar las diferencias permitidas para impedir que una operación administrativa genérica altere progreso o resultados de un trabajo activo.

## Invariantes propuestas

1. La adquisición cambia propietario, lease, vencimiento, intento y token en una transacción; el token nunca disminuye, incluso tras cancelar o reintentar.
2. Renovación y mutación comprueban trabajo, propietario, lease, token y vencimiento usando el reloj PostgreSQL. La fila de coordinación queda bloqueada hasta confirmar la misma transacción que modifica `app_state`.
3. La API puede solicitar cancelación sin poseer el lease, pero no publicar resultados por cuenta de un Worker. Una solicitud pendiente debe impedir una nueva adquisición.
4. La recuperación solo afecta leases vencidos. Si hay efectos externos ambiguos, conservar evidencia y solicitar intervención humana en vez de declarar éxito o repetir sin conciliación.
5. El orden de bloqueo debe ser idéntico en todas las transacciones que toquen coordinación y `app_state`, para evitar interbloqueos. Migración, inicialización, programación y cancelación deben respetarlo.
6. La pérdida del lease cancela el procesamiento; una escritura tardía debe fallar incluso si el proceso todavía sigue vivo. Las peticiones externas ya iniciadas pueden completarse y requieren reconciliación idempotente.

## Alternativas y decisión pendiente de integración

Una tabla de coordinación separada conserva el contrato JSONB de Angular, pero exige actualizar en una transacción tanto la tabla como `app_state` y cerrar todas las rutas genéricas de escritura. Normalizar también las ejecuciones reduce el ámbito de bloqueo y facilita `SKIP LOCKED`, pero requiere migrar y sincronizar los datos existentes y adaptar todos los lectores. La tabla separada es una etapa de compatibilidad razonable únicamente si se impide el acceso sin fencing a las mutaciones del Worker y se prueba la transición con datos previos. Ninguna alternativa proporciona por sí sola exactamente una ejecución de efectos externos.
