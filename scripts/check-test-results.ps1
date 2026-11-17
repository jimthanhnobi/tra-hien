param([string]$Directory = "artifacts/test-results")
$ErrorActionPreference = "Stop"
$files = @(Get-ChildItem -LiteralPath $Directory -Filter *.trx -Recurse)
if ($files.Count -ne 3) { throw "Expected results from all three backend test projects." }
foreach ($file in $files) {
    [xml]$report = Get-Content -LiteralPath $file.FullName -Raw
    $counts = $report.TestRun.ResultSummary.Counters
    if ([int]$counts.total -le 0 -or [int]$counts.executed -ne [int]$counts.total -or
        [int]$counts.passed -ne [int]$counts.total) {
        throw "Failed, skipped or empty test run: $($file.Name)"
    }
    Write-Output "$($file.Name): $($counts.passed) passed, zero skipped."
}
