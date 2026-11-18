param(
    # Optional disposable Testcontainers target. Omit for the dev Compose service.
    [string]$Container,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
$previousConnection = $env:ConnectionStrings__Default
$previousEncoding = $OutputEncoding
try {
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $preflight = Get-Content -Raw -LiteralPath infra/postgres/preflight.sql
    $dockerArgs = if ($Container) { @('exec', '-i', $Container) } else {
        @('compose', '-f', 'infra/compose.dev.yml', 'exec', '-T', 'postgres')
    }
    # Expand credentials only inside the controlled DB container, not argv/logs.
    $login = 'export PGHOST=127.0.0.1 PGUSER=tp_owner PGDATABASE="$TP_DATABASE" PGPASSWORD="$TP_OWNER_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1'
    $preflight | & docker @dockerArgs sh -c $login
    if ($LASTEXITCODE -ne 0) { throw 'Role/ownership preflight failed. Run infra/postgres/init-roles.sql; stop on unexpected ownership/privileges.' }
    if ($PreflightOnly) { return }
    # Generate SQL offline. Never pass a connection/password to EF arguments.
    $env:ConnectionStrings__Default = 'Host=127.0.0.1;Port=1;Database=design_only;Username=tp_owner;Password=unused'
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed' }
    dotnet restore backend/src/TeaPremium.Infrastructure --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Infrastructure locked restore failed' }
    dotnet build backend/src/TeaPremium.Infrastructure --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Infrastructure build failed' }
    $sqlPath = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
    try {
        dotnet ef migrations script --idempotent --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --output $sqlPath
        if ($LASTEXITCODE -ne 0) { throw 'Migration SQL generation failed' }
        # Recheck in the same owner session immediately before the whole chain.
        ($preflight + "`n" + (Get-Content -Raw -LiteralPath $sqlPath)) | & docker @dockerArgs sh -c $login
        if ($LASTEXITCODE -ne 0) { throw 'Migration failed. Do not start API; inspect migration history with operator.' }
    } finally {
        if ($sqlPath -and (Test-Path -LiteralPath $sqlPath)) { Remove-Item -LiteralPath $sqlPath }
    }
    Write-Output 'Migrations applied using tp_owner. Configure runtime as tp_app.'
} finally {
    $env:ConnectionStrings__Default = $previousConnection
    $OutputEncoding = $previousEncoding
    Pop-Location
}
