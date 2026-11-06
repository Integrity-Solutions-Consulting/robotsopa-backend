# Instalador de Bot

En Windows x64 instale el SDK .NET 10 e Inno Setup 6 de https://jrsoftware.org/isdl.php. Desde PowerShell ejecute `./installer/Build-Bot.ps1` en el repositorio backend. El instalador se genera en `installer/output/Bot-Setup.exe`. La publicación es autocontenida e incluye el runtime .NET; el agente utiliza Microsoft Edge instalado en el equipo. La configuración cifrada por usuario se guarda en `%LOCALAPPDATA%\Bot` y no se elimina al actualizar ni desinstalar. Para validar una conexión se necesita una API HTTPS y una identidad de agente habilitada.

Para ejecución SRI mediante Bot remoto, configure `SRI_WORKER_REMOTE_SRI=true` en el Worker y PostgreSQL compartido. El Worker debe permanecer separado del agente. Compruebe la configuración real del despliegue antes de iniciarlo.

## Distribuci?n mediante GitHub Releases

Genere y pruebe el instalador desde el commit que se publicar?. Tras el commit y push autorizados, cree una release y adjunte `installer/output/Bot-Setup.exe`. Por ejemplo, con GitHub CLI autenticado y un tag nuevo:

```powershell
./installer/Build-Bot.ps1
gh release create v1.0.0 installer/output/Bot-Setup.exe --repo Integrity-Solutions-Consulting/robotsopa-backend --title "SOPA Windows v1.0.0" --notes "Instalador Windows autocontenido."
```

Compruebe la descarga con una cuenta autorizada y configure su URL HTTPS en `WINDOWS_INSTALLER_URL` del frontend de Dokploy. Un repositorio privado exige acceso y sesi?n de GitHub. No configure la URL antes de comprobarla. Vac?a, la aplicaci?n muestra que a?n no est? publicado. Para actualizar, genere el instalador desde la nueva versi?n, adj?ntelo a una nueva release y actualice la URL; reinicie o redespliegue el frontend. No incluya credenciales en el instalador.
