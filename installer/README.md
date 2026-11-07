# Instalador de Bot

En Windows x64 instale el SDK .NET 10 e Inno Setup 6 de https://jrsoftware.org/isdl.php. Desde PowerShell ejecute `./installer/Build-Bot.ps1` en el repositorio backend. El instalador 1.0.1 se genera en `installer/output/Bot-Setup-1.0.1.exe`. La publicación es autocontenida e incluye el runtime .NET; el agente utiliza Microsoft Edge instalado en el equipo. La configuración cifrada por usuario se guarda en `%LOCALAPPDATA%\Bot` y el instalador no la elimina. Para validar una conexión se necesita una API HTTPS y una identidad de agente habilitada.

Antes de actualizar, detenga el agente desde Bot y cierre la ventana; después ejecute el nuevo instalador. No es necesario desinstalar la versión anterior: se conserva el mismo AppId. El instalador solicita cerrar aplicaciones que bloqueen los archivos y no las reinicia automáticamente. La exclusión de ejecuciones del agente usa un bloqueo por usuario de Windows en `%LOCALAPPDATA%\SRI-Automation\remote-worker.lock`; esto no impide que usuarios distintos de Windows abran agentes independientes. No borre configuración, sesiones, credenciales ni descargas durante la actualización.

Para ejecución SRI mediante Bot remoto, configure `SRI_WORKER_REMOTE_SRI=true` en el Worker y PostgreSQL compartido. El Worker debe permanecer separado del agente. Compruebe la configuración real del despliegue antes de iniciarlo.

## Distribuci?n mediante GitHub Releases

Genere y pruebe el instalador desde el commit que se publicar?. Tras el commit y push autorizados, cree una release y adjunte el archivo de la versión correspondiente. Por ejemplo, con GitHub CLI autenticado y un tag nuevo:

```powershell
./installer/Build-Bot.ps1
gh release create v1.0.1 installer/output/Bot-Setup-1.0.1.exe --repo Integrity-Solutions-Consulting/robotsopa-backend --title "SOPA Windows v1.0.1" --notes "Instalador Windows autocontenido."
```

Compruebe la descarga con una cuenta autorizada y configure su URL HTTPS en `WINDOWS_INSTALLER_URL` del frontend de Dokploy. Un repositorio privado exige acceso y sesi?n de GitHub. No configure la URL antes de comprobarla. Vac?a, la aplicaci?n muestra que a?n no est? publicado. Para actualizar, genere el instalador desde la nueva versi?n, adj?ntelo a una nueva release y actualice la URL; reinicie o redespliegue el frontend. No incluya credenciales en el instalador.
