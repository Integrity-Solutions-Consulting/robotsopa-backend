param([string]$Iscc = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Iscc) { $paths = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe"); $Iscc = $paths | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw "Instale Inno Setup 6 desde https://jrsoftware.org/isdl.php o indique -Iscc con la ruta a ISCC.exe." }
$project = Join-Path $root 'src/Bot/Bot.csproj'
$version = [string]([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'La versión del proyecto Bot no es válida.' }
$issVersion = (Select-String -LiteralPath (Join-Path $PSScriptRoot 'Bot.iss') -Pattern '^#define MyAppVersion "([0-9]+\.[0-9]+\.[0-9]+)"$').Matches.Groups[1].Value
if ($issVersion -ne $version) { throw 'La versión del instalador no coincide con la del proyecto Bot.' }
$publish = Join-Path $PSScriptRoot "publish/$version"
dotnet publish $project -c Release -r win-x64 --self-contained true -m:1 -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de Bot.' }
& $Iscc (Join-Path $PSScriptRoot 'Bot.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falló la creación del instalador.' }
Write-Host "Instalador: $(Join-Path $PSScriptRoot "output/Bot-Setup-$version.exe")"

