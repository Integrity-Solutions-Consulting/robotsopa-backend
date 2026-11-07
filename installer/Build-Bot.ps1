param([string]$Iscc = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Iscc) { $paths = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe"); $Iscc = $paths | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw "Instale Inno Setup 6 desde https://jrsoftware.org/isdl.php o indique -Iscc con la ruta a ISCC.exe." }
$publish = Join-Path $PSScriptRoot 'publish'
dotnet publish (Join-Path $root 'src/Bot/Bot.csproj') -c Release -r win-x64 --self-contained true -m:1 -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de Bot.' }
& $Iscc (Join-Path $PSScriptRoot 'Bot.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falló la creación del instalador.' }
Write-Host "Instalador: $(Join-Path $PSScriptRoot 'output/Bot-Setup.exe')"

