# API independiente en Dokploy (Dockerfile)

## Límite HTTP de documentos del agente

La API admite como máximo 54.000.000 bytes para `POST /execution-agent/v1/{jobId}/files`: dos archivos de hasta 20.000.000 bytes cada uno, codificados en Base64, más metadatos JSON. Los demás endpoints conservan el límite de 20.000.000 bytes. El límite individual no sustituye las validaciones de XML, PDF ni de propiedad del trabajo.

El Nginx del frontend de este repositorio sirve archivos estáticos y no actúa como proxy de esa ruta. Si existe un proxy externo delante de la API con límite de cuerpo, debe admitir al menos 54.000.000 bytes para esa ruta; por ejemplo, en Nginx `client_max_body_size 54m` es suficiente. Si rechaza antes de llegar a la API, el HTTP 413 aparece en el proxy y no en el registro de la API. No se ha comprobado desde este repositorio el límite efectivo del proxy de producción.

Los rechazos de la API se registran con operación, HTTP y un código sin contenido de documentos, credenciales, identificadores ni datos personales. `LEASE_EXPIRED_OR_CHANGED` indica pérdida de propiedad; `FILE_SIZE_INVALID`, tamaño individual; `REQUEST_TOO_LARGE`, cuerpo excesivo; `FILE_OUTSIDE_AUTHORIZED_JOB`, alcance no autorizado; `XML_INVALID` o `DOCUMENT_OR_REQUEST_INVALID`, validación; y `STORAGE_UNAVAILABLE`, fallo de almacenamiento. Un rechazo del proxy, una desconexión o un error previo a la lectura del cuerpo pueden impedir que la API reciba el detalle.

Estado: preparación parcial; no se ha verificado el despliegue ni la automatización SRI. Antes de producción valide los permisos, las pruebas y las limitaciones indicadas abajo.

## Panel de Dokploy

1. Cree una aplicación para el backend. Seleccione Dockerfile, contexto `back` y Dockerfile `Dockerfile` si el repositorio contiene `back/`; si el repositorio separado comienza en `back/`, use contexto `.` y Dockerfile `Dockerfile`. No seleccione Compose para este despliegue.
2. Configure las variables siguientes en Environment y un dominio HTTPS para la API con puerto interno **8080**. Configure una sola réplica y una política de reinicio. La comprobación de salud es `/health`, sin autenticación.
3. Conserve los cuatro volúmenes existentes y sus datos, montados en `/app/storage`, `/app/downloads`, `/app/sessions` y `/app/credentials`. Verifique que el usuario `app` de la imagen (UID 1654 en la imagen oficial .NET) pueda leer y escribir en ellos. Un montaje existente oculta los permisos preparados en la imagen: si no es escribible, solicite al administrador del servidor que ajuste los permisos sin borrar los datos. La imagen no modifica permisos del volumen en el arranque.
4. Despliegue, compruebe `/health`, inicie sesión y pruebe con datos existentes; reinicie y repita. Un contenedor sano no verifica la integración SRI.
5. Para el frontend cree otra aplicación Dockerfile con contexto `front` (o `.` si es repositorio independiente), puerto 80 y `API_BASE_URL=https://api.ejemplo.com`. Configure HTTPS y reinicie el frontend cuando cambie esa variable. No ponga secretos allí.

## Environment de API

Obligatorias:

- `SRI_ADMIN_USERNAME`: identificador de acceso del administrador inicial.
- `SRI_ADMIN_PASSWORD`: contraseña inicial de al menos 16 caracteres. No actualiza la contraseña ya guardada en la base de datos.
- `Cors__AllowedOrigins__0`: origen HTTPS exacto del frontend, sin barra final. Añada índices adicionales para otros orígenes autorizados.
- `SRI_COOKIE_SECURE=true`: necesario para cookies entre sitios HTTPS.
- `ASPNETCORE_ENVIRONMENT=Production`.

La imagen incluye estos valores modificables: `ASPNETCORE_URLS=http://+:8080`, `SRI_STATE_PATH=/app/storage`, `SRI_OUTPUT_PATH=/app/downloads`, `SRI_SESSION_PATH=/app/sessions`, `SRI_CREDENTIAL_PATH=/app/credentials`, `SRI_PORTAL_PROFILE=/app/config/sri-portal.json`. No cambie rutas sin migrar y montar sus datos. No configure valores vacíos.

La identidad inicial interna sigue llamándose `Cristopher`: `SRI_ADMIN_USERNAME` es un alias de acceso a esa cuenta existente, no cambia su nombre en SQLite. No reinicialice la base de datos para cambiarla. Hay cuentas iniciales adicionales sin contraseña que deben revisarse antes de producción.

## Claves

No guarde claves en Git ni en imágenes. Prefiera archivos secretos montados con lectura para UID 1654 y establezca la variable de archivo correspondiente; Base64 en Environment es alternativa. El valor Base64, cuando existe, prevalece sobre el archivo. No genere ni sustituya automáticamente las claves originales: respaldarlas por separado es indispensable para recuperar datos cifrados.

- `SRI_CREDENTIAL_KEY_FILE` apunta a `credential.key` binario de exactamente 32 bytes, o configure `SRI_CREDENTIAL_KEY_BASE64` con esos mismos 32 bytes codificados. Se necesita para leer o guardar credenciales SRI; la API puede arrancar sin ella. Una clave configurada incorrectamente impide arrancar. Conserve la clave original para credenciales existentes.
- `SRI_SESSION_KEY_FILE` o `SRI_SESSION_KEY_BASE64`: clave binaria de 32 bytes para sesiones SRI cifradas; se requiere al activar el worker. No es necesaria para iniciar la API. No es la clave de sesión del panel.

## Persistencia y límites

`storage` contiene `state.json`, su respaldo y `panel.db`: trabajos, configuraciones, auditoría, RUC, usuarios y asignaciones dependen de ambos archivos. `downloads` contiene documentos y reportes; `credentials` y `sessions` contienen datos SRI cifrados. Haga copias coherentes de los cuatro volúmenes y de las claves por separado, con la aplicación detenida, y pruebe la restauración. No borre volúmenes al actualizar. Respaldar solo SQLite es insuficiente.

Use una sola réplica API, y como máximo un worker, en el mismo host y con almacenamiento compartido cuyo bloqueo de archivos funcione. SQLite y JSON no proporcionan una cola distribuida ni transacciones conjuntas: no use réplicas distribuidas ni almacenamiento de red sin validación específica. Los cambios en JSON y SQLite pueden quedar desincronizados ante un fallo. Las sesiones del panel están en memoria y se invalidan al reiniciar; varias réplicas no comparten sesiones. El frontend y la API requieren una prueba real de cookies entre sus dominios y configuración del navegador.

El Worker comparte PostgreSQL y los volumenes requeridos con la API. Antes de produccion deben validarse recuperacion, exclusividad y reintentos. La descarga real del SRI en Linux sigue pendiente de comprobacion.

Los archivos Compose se conservan para flujos alternativos y locales; no son el método principal aquí. La exposición pública, autorización por cuenta, CSRF, permisos, respaldo y restauración requieren revisión y pruebas antes de usar datos reales.


## Worker y progreso

El Dockerfile de API no ejecuta el Worker. Para procesar trabajos despliegue además el servicio `worker` de `compose.dokploy.yml` desde este repositorio, o una aplicación independiente usando `Dockerfile.worker` y contexto `.`. Use una sola instancia y la misma `ConnectionStrings__DefaultConnection` que la API, con la base ya inicializada. Comparta `/app/storage`, `/app/downloads` y `/app/sessions` con la API; monte el perfil y la clave de sesión original como indica Compose. El Worker necesita acceso de escritura al estado PostgreSQL y a esos vol?menes. Configure `SRI_WORKER_PAUSED=false` para habilitar el procesamiento: Compose deja la pausa activada por defecto. Ajuste también las variables de SharePoint cuando corresponda. No sustituya claves ni borre datos existentes.

El heartbeat se actualiza cada cinco segundos mientras el proceso puede escribir en PostgreSQL. El estado en la interfaz usa un umbral de 15 segundos; el healthcheck del contenedor usa 30 segundos y considera no saludable un Worker pausado. Confirme en Dokploy que el servicio existe y revise sus logs, conectividad y montajes: el estado local de este repositorio no demuestra qué contenedores están desplegados. La barra de Historial cuenta consultas SRI confirmadas, no la finalización de las descargas, reportes o entregas.
