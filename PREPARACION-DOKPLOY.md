# Preparación para una prueba en Dokploy

Este documento describe configuraciones a comprobar; no acredita que la instalación activa las tenga ni autoriza cambiar servicios o datos existentes. La implementación sigue requiriendo completar extractores, reportes y pruebas reales antes de considerarse lista para despliegue.

## Aplicaciones y configuración

- Frontend independiente: dominio `https://bot.dokploy.integritysolutions.com.ec`, puerto interno 80, variable `API_BASE_URL=https://botisc.dokploy.integritysolutions.com.ec`. `WINDOWS_INSTALLER_URL` es opcional y solo debe establecerse con una URL HTTPS de un instalador revisado y publicado.
- API: dominio `https://botisc.dokploy.integritysolutions.com.ec`, puerto interno 8080 y healthcheck `/health`. En despliegue por Compose, `FRONTEND_ORIGIN=https://bot.dokploy.integritysolutions.com.ec` alimenta `Cors__AllowedOrigins__0`; en una aplicación independiente configure directamente `Cors__AllowedOrigins__0` con ese origen. Use `SRI_COOKIE_SECURE=true` y `ASPNETCORE_ENVIRONMENT=Production`.
- API y Worker usan la misma `ConnectionStrings__DefaultConnection` y el mismo almacenamiento persistente de documentos. Las credenciales deben configurarse mediante secretos del entorno, sin incluirlas en imágenes o Git. La conexión PostgreSQL, permisos y esquema deben estar ya preparados y verificados; no se debe modificar una base de producción para esta prueba.
- En Compose, el volumen `downloads` se monta en `/app/downloads` para API y Worker y contiene tanto reportes como sus directorios `.versiones`. `state`, `sessions` y `credentials` también son persistentes; `credentials` se monta de solo lectura en el Worker. Si se despliegan como aplicaciones independientes, sus montajes deben apuntar a los mismos volúmenes reales y soportar bloqueos de archivos y reemplazo atómico.
- `SRI_CONFIG_DIR` y `SRI_SECRET_DIR` identifican los directorios de configuración y secretos en el host. Preserve las claves existentes de credenciales y sesiones. Configure `SRI_REMOTE_EXECUTION_ENABLED` y `SRI_WORKER_PAUSED` según la operación autorizada; Compose deja la ejecución remota desactivada y el Worker pausado por defecto.
- Configure un proxy confiable o el modo aislado únicamente según la topología real del proxy TLS; no habilite ambos mecanismos a la vez. Consulte `DOKPLOY.md` y `WORKER_DOKPLOY.md` para los requisitos de montaje y claves.

## Secuencia futura de pruebas

1. Revise los bloqueos funcionales pendientes, haga respaldo verificable de datos y claves y confirme que existen volúmenes persistentes compartidos.
2. Con la base y configuración de prueba ya existentes, actualice la API y compruebe `/health`. Actualice el Worker y verifique conectividad, heartbeat y montajes. No elimine volúmenes ni cambie credenciales existentes.
3. Actualice el frontend con la URL exacta de la API y compruebe inicio de sesión, cookies, CORS y descarga de reportes desde el navegador.
4. Genere el instalador usando `installer/Build-Bot.ps1` con .NET 10 e Inno Setup 6 oficiales. Revise el contenido, conserve el nombre público Bot y compruebe la conexión del agente con la API HTTPS de prueba.
5. Una vez que el usuario autorice pruebas reales, procese un conjunto pequeño de documentos de prueba y compare el XML original con el Excel usando `VALIDACION-COMPROBANTES.md`. Verifique seis tipos, detalles, impuestos, fechas, reportes, respaldos y reinicio de servicios. No comparta documentos privados ni claves.

No se han ejecutado aquí pruebas reales de los dominios, Docker desplegado, cookies, base remota o SRI. Los resultados de una prueba en Dokploy no equivalen a aprobación para producción.
