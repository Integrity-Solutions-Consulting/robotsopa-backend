# Backend independiente en Dokploy

Este repositorio contiene la API y el worker. Seleccione Docker Compose y `compose.dokploy.yml` desde la raíz de este repositorio. El frontend se despliega en otra aplicación y otro repositorio. Asigne el dominio HTTPS al servicio `api`, puerto 8080. El worker no recibe dominio.

En Environment configure `FRONTEND_ORIGIN` con el origen HTTPS exacto del frontend, `SRI_ADMIN_USERNAME`, `SRI_ADMIN_PASSWORD`, `SRI_CONFIG_DIR` y `SRI_SECRET_DIR`; consulte `.env.example`. La contraseña debe tener al menos 16 caracteres. El archivo Compose configura CORS y cookies seguras, y mantiene el worker pausado por defecto. Para activarlo configure `SRI_WORKER_PAUSED=false`.

El directorio `SRI_CONFIG_DIR` debe contener `sri-portal.json` (hay un ejemplo en `config/`). En `SRI_SECRET_DIR` deben existir `agent.key` (texto hexadecimal de al menos 64 caracteres), `credential.key` y `session.key` (32 bytes cada una). Proteja estos archivos y conserve las claves originales cuando migre datos. El Compose crea volúmenes persistentes separados para estado SQLite, documentos, sesiones y credenciales. API y worker deben ejecutarse en el mismo host y compartir esos volúmenes; use una sola instancia de cada uno. No elimine los volúmenes durante actualizaciones.

El perfil SRI incluido y la ejecución real contra el portal no se han validado. La API funciona sin frontend; la automatización requiere el worker y, para flujos interactivos, el agente correspondiente.

El archivo `.env.example` enumera todas las variables que requiere este Compose; las rutas internas y las variables específicas de cada servicio ya están declaradas en `compose.dokploy.yml`. `api.environment.example` es una alternativa para la imagen Dockerfile de API sola y no inicia el worker.

El agente Windows actual solo permite una URL loopback para `SRI_AGENT_API`. Por eso todavía no puede conectarse directamente al dominio público del backend. El despliegue independiente de API y frontend no resuelve ese componente: los flujos SRI que necesitan el agente no están listos para uso remoto hasta adaptar y validar el agente. No se debe prometer que activar el worker, por sí solo, habilite esas consultas.
