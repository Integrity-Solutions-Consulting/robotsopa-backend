# Instalador de Bot

En Windows x64 instale el SDK .NET 10 e Inno Setup 6 de https://jrsoftware.org/isdl.php. Desde PowerShell ejecute `./installer/Build-Bot.ps1` en el repositorio backend. El instalador se genera en `installer/output/Bot-Setup.exe`. La publicación es autocontenida e incluye el runtime .NET; el agente utiliza Microsoft Edge instalado en el equipo. La configuración cifrada por usuario se guarda en `%LOCALAPPDATA%\Bot` y no se elimina al actualizar ni desinstalar. Para validar una conexión se necesita una API HTTPS y una identidad de agente habilitada.

Para ejecución SRI mediante Bot remoto, configure `SRI_WORKER_REMOTE_SRI=true` en el Worker y PostgreSQL compartido. El Worker debe permanecer separado del agente. Compruebe la configuración real del despliegue antes de iniciarlo.
