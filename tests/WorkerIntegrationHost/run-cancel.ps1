param(
    [Parameter(Mandatory=$true)][string]$ConnectionString,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$JobId = 'test-cancel-4i'
)
$ErrorActionPreference = 'Stop'
$parsed = New-Object System.Data.Common.DbConnectionStringBuilder
$parsed.set_ConnectionString($ConnectionString)
if ($parsed.get_Item('Host') -notin @('127.0.0.1','localhost') -or -not ([string]$parsed.get_Item('Database')).StartsWith('botsopa_test_')) { throw 'Only localhost test databases are permitted.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$dll = (Resolve-Path (Join-Path $PSScriptRoot 'bin/Debug/net10.0/WorkerIntegrationHost.dll')).Path
$dir = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
if (Get-ChildItem -LiteralPath $dir -Force) { throw 'Output directory must be empty.' }
$env:ConnectionStrings__DefaultConnection = $ConnectionString
$p = Start-Process dotnet -ArgumentList @($dll, $dir, $JobId) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dir 'worker.out') -RedirectStandardError (Join-Path $dir 'worker.err')
try {
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Get-ChildItem -LiteralPath $dir -Filter 'owner-*.json') -and (Get-Date) -lt $deadline) { if ($p.HasExited) { throw 'Worker exited before acquisition.' }; Start-Sleep -Milliseconds 200 }
    if (-not (Get-ChildItem -LiteralPath $dir -Filter 'owner-*.json')) { throw 'Acquisition timeout.' }
    Start-Sleep -Seconds 17
    if ($p.HasExited) { throw 'Worker exited during renewal observation.' }
    Write-Output "PID=$($p.Id); ownership record and logs: $dir"
    Write-Output 'Worker remains running; request cancellation via the test database before terminating it.'
} catch { if (-not $p.HasExited) { Stop-Process -Id $p.Id }; throw }
