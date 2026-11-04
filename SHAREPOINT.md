# SharePoint Online (integración en validación)

La integración no se ha probado contra Microsoft 365 y no debe considerarse lista para producción. Mantenga el worker en pausa mientras prepara un entorno de prueba aislado. No se requiere cambiar ni migrar usuarios o documentos existentes.

## Variables de Dokploy

Configure las mismas variables en API y worker (el Compose de `back` ya las transmite):

| Variable | Valor |
|---|---|
| `SRI_SHAREPOINT_ENABLED` | `true` para activar; por defecto `false` |
| `SRI_SHAREPOINT_TENANT_ID` | Identificador del tenant Entra |
| `SRI_SHAREPOINT_CLIENT_ID` | Identificador de aplicación |
| `SRI_SHAREPOINT_CLIENT_SECRET` | Secreto de aplicación en la configuración privada de Dokploy |
| `SRI_SHAREPOINT_SITE_ID` | ID de Graph del sitio |
| `SRI_SHAREPOINT_DRIVE_ID` | ID de Graph de la biblioteca |
| `SRI_SHAREPOINT_FOLDER` | Carpeta relativa en la biblioteca; puede estar vacía |

Alternativamente, fuera del Compose incluido, se puede montar un archivo privado y configurar `SRI_SHAREPOINT_CLIENT_SECRET_FILE`; tiene prioridad sobre la variable de secreto. No incluya credenciales en Git ni en Angular. Mantenga compartido el volumen `downloads` entre API y worker para el agente y la cola, y PostgreSQL persistente. La cola se almacena en `app_state`, en la misma transacción que el manifiesto. El worker pausado tampoco consume la cola.

## Preparar una prueba aislada en Microsoft 365

1. Cree un sitio y una biblioteca de pruebas, sin datos productivos. Registre una aplicación en Microsoft Entra y anote tenant ID y client ID. Cree una credencial de aplicación y guárdela en Dokploy; no la copie a la conversación ni al repositorio.
2. Agregue el permiso **de aplicación** Microsoft Graph `Sites.Selected` y conceda el consentimiento administrativo. Un administrador con autorización suficiente debe conceder a la aplicación el rol `write` específicamente sobre el sitio de prueba: `POST /sites/{site-id}/permissions`, con `roles: ["write"]` y `grantedToIdentities` identificando la aplicación por su client ID. La concesión es una operación administrativa separada del consentimiento. Consulte [permisos seleccionados](https://learn.microsoft.com/graph/permissions-selected-overview) y [crear permiso de sitio](https://learn.microsoft.com/graph/api/site-post-permissions).
3. Obtenga el ID de sitio con Graph `GET /sites/{hostname}:/sites/{ruta}` y la biblioteca con `GET /sites/{site-id}/drives`; use el `id` de la biblioteca elegida. Estas consultas administrativas pueden requerir permisos distintos a los de la aplicación de ejecución. Véanse [obtener sitio](https://learn.microsoft.com/graph/api/site-getbypath) y [listar bibliotecas](https://learn.microsoft.com/graph/api/drive-list).
4. En un despliegue aislado, configure las variables y vuelva a construir API y worker. Active SharePoint y el worker solo allí. Use un RUC y comprobantes ficticios y una cuenta de prueba autorizada; no use una consulta real del SRI sin autorización.
5. Verifique que la cola registre pendiente, intento y confirmación, que el archivo aparezca bajo `carpeta/RUC/año/mes/tipo`, que la API lo descargue solo para una cuenta autorizada, y que se elimine el temporal después de la confirmación. Repita con un duplicado y un contenido distinto y pruebe reinicio y limitación de Graph. No habilite producción hasta validar todos los casos.

Los errores no transitorios quedan en estado `Failed`. El administrador puede consultar `GET /api/v1/sharepoint/uploads` y solicitar un reintento con `POST /api/v1/sharepoint/uploads/{id}/retry` después de resolver la causa; la interfaz Angular todavía no incluye controles para estas rutas. La carga usa sesiones de Graph con conflicto `fail` y verifica el SHA-256 leyendo el contenido remoto antes de confirmar. Una respuesta perdida se recupera comprobando el archivo existente en el siguiente intento. Los reportes Excel y archivos existentes permanecen en almacenamiento local; esta integración solo pone en cola los comprobantes que pasan por `DownloadPipeline` después de habilitarla.
