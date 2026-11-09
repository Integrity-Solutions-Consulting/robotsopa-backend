param()
$ErrorActionPreference = 'Stop'
$connection = $env:SRI_ISOLATED_TEST_POSTGRES
if ([string]::IsNullOrWhiteSpace($connection)) { throw 'Configure SRI_ISOLATED_TEST_POSTGRES con una base local desechable llamada sopa_zip_test.' }
if ($connection -match '(?i)bot_db' -or $connection -notmatch '(?i)(?:^|;)\s*(?:database|initial catalog)\s*=\s*sopa_zip_test\s*(?:;|$)' -or $connection -notmatch '(?i)(?:^|;)\s*(?:host|server)\s*=\s*(?:localhost|127\.0\.0\.1|\[::1\]|::1)\s*(?:;|$)') {
    throw 'Solo se permite una conexión local explícita a la base desechable sopa_zip_test; no se imprimirá la conexión.'
}
$project = Join-Path $PSScriptRoot '..\tests\SriAutomation.Tests\SriAutomation.Tests.csproj'
Write-Host 'Ejecutando pruebas ZIP sobre la base local desechable. Cada prueba usa su propio esquema temporal.'
& dotnet test $project --no-restore --filter 'FullyQualifiedName~PostgresZipJobStoreTests|FullyQualifiedName~DocumentZipTests|FullyQualifiedName~ZipServiceDoubleTests|FullyQualifiedName~MonthlyReportContractTests'
if ($LASTEXITCODE -ne 0) { throw "Las pruebas terminaron con código $LASTEXITCODE." }
