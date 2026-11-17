$ErrorActionPreference = "Stop"
$previousConnection = $env:ConnectionStrings__Default
try {
    # Unreachable loopback port: script generation must not access any real database.
    $env:ConnectionStrings__Default = "Host=127.0.0.1;Port=1;Database=ci_design_only;Username=ci_dummy;Password=ci_dummy"
    New-Item -ItemType Directory -Force artifacts | Out-Null
    Remove-Item -LiteralPath artifacts/migrations.sql -ErrorAction SilentlyContinue
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "Tool restore failed" }
    dotnet ef migrations script --idempotent --no-build --context AppDbContext --project backend/src/TeaPremium.Infrastructure --startup-project backend/src/TeaPremium.Infrastructure --output artifacts/migrations.sql
    if ($LASTEXITCODE -ne 0) { throw "EF SQL generation failed" }
    if (!(Test-Path artifacts/migrations.sql) -or (Get-Item artifacts/migrations.sql).Length -eq 0) {
        throw "EF produced empty SQL"
    }
    Write-Output "Generated artifacts/migrations.sql ($((Get-Item artifacts/migrations.sql).Length) bytes)."
} finally {
    $env:ConnectionStrings__Default = $previousConnection
}
