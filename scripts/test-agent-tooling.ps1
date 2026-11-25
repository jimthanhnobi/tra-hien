<#
.SYNOPSIS
  Self-test for the agent tooling scripts (verify-linux.ps1, candidate-manifest.ps1, task-integrate.ps1, and local docs flow).
.DESCRIPTION
  Runs entirely in throwaway git repositories under the system temp directory with a fake `docker`
  command first on PATH and an isolated TRA_HIEN_TEST_LOCK_PATH, so it never touches the real worktree,
  its index, real host locks, or the Docker engine.
  Covers: filtered snapshot excludes git-ignored files, socket mounted only for backend/full, run by
  image ID, mode normalisation, exit-code propagation, -WriteBack failure handling, filter & coverage
  protection, docs-only local verification, manifest -Both/-RequireBase/-PublishMode hardening,
  and multi-process host-level PostgreSQL lock contention/release/crash/residual-workload handling.
.EXAMPLE
  ./scripts/test-agent-tooling.ps1
#>
$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$id = [guid]::NewGuid().ToString('N').Substring(0, 12)
$tmp = Join-Path ([IO.Path]::GetTempPath()) "tra-hien-tooling-$id"
$bin = Join-Path ([IO.Path]::GetTempPath()) "tra-hien-tooling-$id-bin"
$fakeLog = Join-Path $bin 'docker-calls.log'
$defaultTestLock = Join-Path $bin 'selftest-heavy-pg.lock'
$originalPath = $env:PATH
$originalDockerfile = $env:FAKE_DOCKERFILE
$originalTestLock = $env:TRA_HIEN_TEST_LOCK_PATH
$script:failures = 0
$script:passes = 0

function Assert([bool]$Condition, [string]$Name) {
    if ($Condition) { $script:passes++; Write-Output "PASS  $Name"; return }
    $script:failures++
    Write-Output "FAIL  $Name"
    if ($script:last) {
        $text = "rc=$($script:last.Rc)`n$($script:last.Output)`n--- docker calls ---`n$($script:last.Calls)"
        Write-Output ($text.Substring(0, [Math]::Min(1800, $text.Length)) -replace '(?m)^', '      | ')
    }
}
function Write-Text([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Invoke-Tool([string]$Script, [string[]]$Arguments, [hashtable]$Environment = @{}, [string]$Root = $tmp) {
    $saved = @{}
    foreach ($key in $Environment.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        if ($null -eq $Environment[$key]) {
            Remove-Item "env:$key" -ErrorAction SilentlyContinue
        } else {
            Set-Item "env:$key" $Environment[$key]
        }
    }
    Remove-Item -LiteralPath $fakeLog -ErrorAction SilentlyContinue
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts/$Script") @Arguments 2>&1 | Out-String
    $rc = $LASTEXITCODE
    foreach ($key in $Environment.Keys) {
        if ($null -eq $saved[$key]) { Remove-Item "env:$key" -ErrorAction SilentlyContinue }
        else { Set-Item "env:$key" $saved[$key] }
    }
    $calls = if (Test-Path -LiteralPath $fakeLog) { Get-Content -LiteralPath $fakeLog -Raw } else { '' }
    $script:last = [pscustomobject]@{ Rc = $rc; Output = $output; Calls = $calls }
    return $script:last
}
function Invoke-TmpGit { & git -c user.name=selftest -c 'user.email=selftest@example.invalid' -C $tmp @args 2>&1 | Out-Null }
function Invoke-GitAt([string]$Dir) { & git -c user.name=selftest -c 'user.email=selftest@example.invalid' -C $Dir @args 2>&1 | Out-Null }

function Copy-ToolingScripts([string]$TargetRepo) {
    foreach ($name in 'verify-linux.ps1', 'verify-linux.sh', 'candidate-manifest.ps1', 'task-integrate.ps1', 'ci-fastpath.py') {
        Write-Text (Join-Path $TargetRepo "scripts/$name") ([IO.File]::ReadAllText((Join-Path $repoRoot "scripts/$name")))
    }
    Write-Text (Join-Path $TargetRepo 'infra/Dockerfile.tools') ([IO.File]::ReadAllText((Join-Path $repoRoot 'infra/Dockerfile.tools')))
}

function Start-BackgroundTool([string]$RootDir, [string]$Script, [string[]]$Arguments, [hashtable]$EnvOverrides, [string]$StdOutFile, [string]$StdErrFile) {
    $psi = [Diagnostics.ProcessStartInfo]::new('powershell')
    $psi.WorkingDirectory = $RootDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $RootDir "scripts/$Script")) + $Arguments
    $escaped = @($argList | ForEach-Object {
        if ($_ -eq '') { '""' }
        elseif ($_ -match '[\s"]') { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
        else { $_ }
    })
    $psi.Arguments = $escaped -join ' '
    foreach ($key in $EnvOverrides.Keys) {
        if ($null -eq $EnvOverrides[$key]) { $psi.EnvironmentVariables.Remove($key) }
        else { $psi.EnvironmentVariables[$key] = "$($EnvOverrides[$key])" }
    }
    $proc = [Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $proc.Start() | Out-Null
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    return [pscustomobject]@{
        Process = $proc
        OutTask = $outTask
        ErrTask = $errTask
    }
}

$docsRepo = "$tmp-docs"
$f01Repo = "$tmp-f01"
$f01Outside = "$tmp-f01-outside"
$f02Repo = "$tmp-f02"
$f03Repo = "$tmp-f03"
$topoRepo = "$tmp-topo"
$lockRepo = "$tmp-lock"
$lockWt1 = "$tmp-lock-wt1"
$lockWt2 = "$tmp-lock-wt2"
$bgProcs = @()
$createdJunctions = @()

try {
    # Throwaway repository with an ignored secret and committed API outputs.
    Copy-ToolingScripts $tmp
    Write-Text (Join-Path $tmp '.gitignore') ".env`nartifacts/`n"
    Write-Text (Join-Path $tmp 'infra/.env') "SECRET=do-not-copy`n"
    Write-Text (Join-Path $tmp 'packages/api-client/openapi.json') "ORIGINAL-OPENAPI`n"
    Write-Text (Join-Path $tmp 'packages/api-client/src/generated/schema.ts') "ORIGINAL-SCHEMA`n"
    Invoke-TmpGit init -q
    Invoke-TmpGit add -A
    Invoke-TmpGit commit -q -m base

    # Fake docker: records arguments, inspects the mounted snapshot, supports ps/hold/docs-python/outputs.
    Write-Text (Join-Path $bin 'docker.cmd') "@powershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0fake-docker.ps1`" %*`r`n"
    Write-Text (Join-Path $bin 'fake-docker.ps1') @'
$log = if ($env:FAKE_LOG_FILE) { $env:FAKE_LOG_FILE } else { Join-Path $PSScriptRoot 'docker-calls.log' }
Add-Content -LiteralPath $log -Value ("ARGS " + ($args -join ' | '))
if ($args[0] -eq 'ps') {
    if ($env:FAKE_RESIDUAL_CONTAINERS) {
        foreach ($item in ($env:FAKE_RESIDUAL_CONTAINERS -split ';')) {
            if ($item.Trim()) { Write-Output $item.Trim() }
        }
    }
    exit 0
}
if ($args[0] -eq 'image') {
    if (($args -join ' ') -match 'json \.Config\.Labels') {
        $builtHash = Select-String -LiteralPath $log -Pattern '^BUILT_DOCKERFILE_SHA256 (.+)$' | Select-Object -Last 1
        $hash = if ($builtHash) { $builtHash.Matches[0].Groups[1].Value }
        elseif ($env:FAKE_STALE_TOOLS_IMAGE) { 'stale' }
        else { (Get-FileHash -Algorithm SHA256 -LiteralPath $env:FAKE_DOCKERFILE).Hash.ToLowerInvariant() }
        $labels = @{ 'org.tra-hien.tools.dockerfile-sha256' = $hash } | ConvertTo-Json -Compress
        Write-Output "sha256:fakeimage0001 $labels"
    } else { Write-Output 'sha256:fakeimage0001' }
    exit 0
}
if ($args[0] -eq 'build') {
    $recipe = $args[([Array]::IndexOf($args, '-f') + 1)]
    $original = [IO.File]::ReadAllBytes($env:FAKE_DOCKERFILE)
    try {
        if ($env:FAKE_EDIT_LIVE_DOCKERFILE) {
            [IO.File]::WriteAllText($env:FAKE_DOCKERFILE, 'unreviewed live Dockerfile')
        }
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $recipe).Hash.ToLowerInvariant()
        Add-Content -LiteralPath $log -Value "BUILT_DOCKERFILE_SHA256 $hash"
    } finally {
        if ($env:FAKE_EDIT_LIVE_DOCKERFILE) { [IO.File]::WriteAllBytes($env:FAKE_DOCKERFILE, $original) }
    }
    exit 0
}
if ($args[0] -eq 'run') {
    $src = (@($args | Where-Object { $_ -like '*:/src:ro' })[0]) -replace ':/src:ro$', ''
    $out = (@($args | Where-Object { $_ -like '*:/out' })[0]) -replace ':/out$', ''
    Add-Content -LiteralPath $log -Value ("ENV_IN_SNAPSHOT " + (Test-Path -LiteralPath (Join-Path $src 'infra/.env')))
    Add-Content -LiteralPath $log -Value ("SCRIPT_IN_SNAPSHOT " + (Test-Path -LiteralPath (Join-Path $src 'scripts/verify-linux.sh')))
    $snapOpenapi = Join-Path $src 'packages/api-client/openapi.json'
    if (Test-Path -LiteralPath $snapOpenapi) { Add-Content -LiteralPath $log -Value ("OPENAPI_IN_SNAPSHOT " + ([IO.File]::ReadAllText($snapOpenapi)).Trim()) }
    Add-Content -LiteralPath $log -Value ("NEW_IN_SNAPSHOT " + (Test-Path -LiteralPath (Join-Path $src 'notes/new.txt')))
    Add-Content -LiteralPath $log -Value ("UNREVIEWED_IN_SNAPSHOT " + (Test-Path -LiteralPath (Join-Path $src 'notes/unreviewed.txt')))
    if ($env:FAKE_CHECK_FILE) {
        $checkTarget = Join-Path $src $env:FAKE_CHECK_FILE
        $exists = Test-Path -LiteralPath $checkTarget -PathType Leaf
        Add-Content -LiteralPath $log -Value ("CHECK_FILE_EXISTS " + $exists)
        if ($exists) {
            $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $checkTarget).Hash.ToLowerInvariant()
            Add-Content -LiteralPath $log -Value ("CHECK_FILE_SHA256 " + $sha)
        }
    }
    if ($env:FAKE_HOLD_FILE) {
        [IO.File]::WriteAllText($env:FAKE_HOLD_FILE, "STARTED pid=$PID")
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($env:FAKE_RELEASE_FILE -and (Test-Path -LiteralPath $env:FAKE_RELEASE_FILE)) { break }
            Start-Sleep -Milliseconds 100
        }
    }
    if ($args[$args.Count - 1] -eq 'docs' -and $env:FAKE_RUN_DOCS_PYTHON -eq '1') {
        $pyExe = if (Get-Command py -ErrorAction SilentlyContinue) { 'py' } elseif (Get-Command python3 -ErrorAction SilentlyContinue) { 'python3' } else { 'python' }
        & $pyExe (Join-Path $src 'scripts/ci-fastpath.py') --local-inventory (Join-Path $out 'candidate-inventory.json') --root $src
        exit $LASTEXITCODE
    }
    if ($env:FAKE_OUTPUTS -eq '1') {
        New-Item -ItemType Directory -Force -Path (Join-Path $out 'api-client') | Out-Null
        [IO.File]::WriteAllText((Join-Path $out 'api-client/openapi.json'), "FAKE-OPENAPI`n")
        [IO.File]::WriteAllText((Join-Path $out 'api-client/schema.ts'), "FAKE-SCHEMA`n")
    }
    if ($env:FAKE_RUN_RC) { exit [int]$env:FAKE_RUN_RC }
    exit 0
}
exit 0
'@
    $env:PATH = "$bin;$env:PATH"
    $env:FAKE_DOCKERFILE = Join-Path $tmp 'infra/Dockerfile.tools'
    $env:TRA_HIEN_TEST_LOCK_PATH = $defaultTestLock
    $openapi = Join-Path $tmp 'packages/api-client/openapi.json'

    # =====================================================================
    # 1. Existing baseline verify-linux.ps1 & candidate-manifest.ps1 tests
    # =====================================================================
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api')
    Assert ($r.Rc -eq 3) 'api: missing generated outputs fail with exit 3'
    Assert ($r.Output -match 'RESULT=FAIL') 'api: missing outputs report RESULT=FAIL'
    Assert ($r.Output -match 'coverage=NONE') 'api: missing outputs report coverage=NONE'
    Assert ($r.Calls -match 'ENV_IN_SNAPSHOT False') 'snapshot excludes git-ignored infra/.env'
    Assert ($r.Calls -match 'SCRIPT_IN_SNAPSHOT True') 'snapshot contains the untracked-or-tracked script'
    Assert ($r.Calls -notmatch 'docker\.sock') 'api: Docker socket is not mounted'
    Assert ($r.Calls -match 'run \|.*sha256:fakeimage0001') 'container runs by resolved image ID'

    $lock = [IO.File]::Open($openapi, 'Open', 'Read', 'None')
    try { $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api', '-WriteBack') @{ FAKE_OUTPUTS = '1' } } finally { $lock.Dispose() }
    Assert ($r.Rc -eq 3) 'api -WriteBack: copy failure (file locked) fails with exit 3'
    Assert ($r.Output -notmatch 'RESULT=PASS') 'api -WriteBack: copy failure never reports PASS'
    Assert (([IO.File]::ReadAllText($openapi)).Trim() -eq 'ORIGINAL-OPENAPI') 'api -WriteBack: locked target left unchanged'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api', '-WriteBack') @{ FAKE_OUTPUTS = '1' }
    Assert ($r.Rc -eq 0) 'api -WriteBack: success exits 0'
    Assert ($r.Output -match 'RESULT=PASS mode=api coverage=API manifest_sha256=NONE exit_code=0') 'api -WriteBack: emits explicit API coverage and manifest_sha256=NONE'
    Assert (([IO.File]::ReadAllText($openapi)).Trim() -eq 'FAKE-OPENAPI') 'api -WriteBack: target written'
    Assert ($r.Output -match 'hash verified') 'api -WriteBack: destination hash verified'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'Backend')
    Assert ($r.Rc -eq 0) 'Mode Backend (mixed case) accepted'
    Assert ($r.Output -match 'RESULT=PASS mode=backend coverage=FULL_BACKEND manifest_sha256=NONE exit_code=0') 'backend unfiltered: emits RESULT=PASS and coverage=FULL_BACKEND'
    Assert ($r.Calls -match '\| backend\r?\n') 'mode normalised to lowercase for the Linux script'
    Assert ($r.Calls -match 'docker\.sock') 'backend: Docker socket mounted'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{ FAKE_RUN_RC = '37' }
    Assert ($r.Rc -eq 37) 'container exit code propagated (37)'
    Assert ($r.Output -match 'RESULT=FAIL mode=backend coverage=NONE manifest_sha256=NONE exit_code=37') 'failed backend reports coverage=NONE and actual exit_code=37'
    Assert (-not $r.Output.Contains('RESULT=PASS') -and -not $r.Output.Contains('RESULT=FOCUSED_PASS')) 'failed backend never emits success marker'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'frontend', '-WriteBack')
    Assert ($r.Rc -eq 2) '-WriteBack rejected outside api mode'

    # Force-added secret (ignored name, but in the index) is refused on the host before any copy/run.
    Write-Text (Join-Path $tmp 'config/.env') "SECRET=forced`n"
    Invoke-TmpGit add -f -- config/.env
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api')
    Assert ($r.Rc -eq 4 -and $r.Output -match 'secret-like files would enter the container.*config/\.env') 'force-added .env refused on host (exit 4)'
    Assert ($r.Calls -notmatch 'ARGS run') 'force-added .env: container never started'
    Write-Text (Join-Path $tmp 'keys/dev.pem') "-----BEGIN FAKE-----`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api')
    Assert ($r.Rc -eq 4 -and $r.Output -match 'keys/dev.pem') 'untracked *.pem refused on host'
    Invoke-TmpGit rm -q --cached -- config/.env
    Remove-Item -LiteralPath (Join-Path $tmp 'config') -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $tmp 'keys') -Recurse -Force

    # candidate-manifest.ps1: the -WriteBack case above modified openapi.json and schema.ts; add one untracked file.
    $manifest = 'artifacts/m/candidate.manifest'
    Write-Text (Join-Path $tmp 'notes/new.txt') "untracked candidate file`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'HEAD', '-Out', $manifest)
    Assert ($r.Rc -eq 0 -and $r.Output -match 'ENTRIES:\s+3') 'manifest written for two modified + one untracked file'
    Assert ($r.Output -notmatch '\.env') 'manifest never lists git-ignored files'
    $manifestSha = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $tmp $manifest)).Hash.ToLowerInvariant()

    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest)
    Assert ($r.Rc -eq 0 -and $r.Output -match 'CANDIDATE MATCH') 'verify: unchanged working tree matches'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api', '-Manifest', $manifest) @{ FAKE_OUTPUTS = '1' }
    Assert ($r.Rc -eq 0 -and $r.Output -match 'manifest-check: CANDIDATE MATCH') 'verify-linux -Manifest: matching candidate runs and logs manifest'
    Assert ($r.Output -match "RESULT=PASS mode=api coverage=API manifest_sha256=$manifestSha exit_code=0") 'verify-linux -Manifest api: logs manifest_sha256 and coverage=API'
    Assert ($r.Output -match 'source=manifest objects') 'verify-linux -Manifest: snapshot built from manifest git objects'
    Assert ($r.Calls -match 'OPENAPI_IN_SNAPSHOT FAKE-OPENAPI' -and $r.Calls -match 'NEW_IN_SNAPSHOT True') 'verify-linux -Manifest: snapshot holds the reviewed modified and untracked content'
    Assert ($r.Calls -match 'ENV_IN_SNAPSHOT False') 'verify-linux -Manifest: ignored infra/.env not in snapshot'

    # =====================================================================
    # 2. PR-2 Filter Protection & Explicit Coverage Regression Tests
    # =====================================================================
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend', '-Manifest', $manifest, '-Filter', 'FullyQualifiedName~Inventory')
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'filter: -Manifest + nonempty -Filter rejected with exit 2 before Docker'
    Assert ($r.Output -match 'RESULT=FAIL mode=backend coverage=NONE manifest_sha256=NONE exit_code=2') 'filter: -Manifest + -Filter reports RESULT=FAIL coverage=NONE'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'full', '-Manifest', $manifest, '-Filter', 'FullyQualifiedName~Order')
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'filter: -Mode full + -Manifest + -Filter rejected before Docker'

    foreach ($nonTestMode in 'api', 'frontend', 'docs') {
        $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', $nonTestMode, '-Filter', 'FullyQualifiedName~Any')
        Assert ($r.Rc -eq 2 -and -not $r.Calls) "filter: -Mode $nonTestMode rejects -Filter with exit 2 before Docker"
    }

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend', '-Filter', '   ')
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'filter: whitespace-only -Filter rejected with exit 2 before Docker'

    # Test empty string '' and $null for -Filter via in-process PowerShell invocation (PS 5.1 -File strips empty native args)
    $scriptFile = Join-Path $tmp 'scripts/verify-linux.ps1'
    $outEmpty = & powershell -NoProfile -ExecutionPolicy Bypass -Command "& '$scriptFile' -Mode backend -Manifest '$manifest' -Filter ''" 2>&1 | Out-String
    $rcEmpty = $LASTEXITCODE
    Assert ($rcEmpty -eq 0 -and $outEmpty -match "RESULT=PASS mode=backend coverage=FULL_BACKEND manifest_sha256=$manifestSha exit_code=0") 'filter: empty -Filter "" with -Manifest runs unfiltered FULL_BACKEND'

    $outNull = & powershell -NoProfile -ExecutionPolicy Bypass -Command "& '$scriptFile' -Mode backend -Manifest '$manifest' -Filter `$null" 2>&1 | Out-String
    $rcNull = $LASTEXITCODE
    Assert ($rcNull -eq 0 -and $outNull -match "RESULT=PASS mode=backend coverage=FULL_BACKEND manifest_sha256=$manifestSha exit_code=0") 'filter: null -Filter $null with -Manifest runs unfiltered FULL_BACKEND'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend', '-Filter', 'FullyQualifiedName~Inventory')
    Assert ($r.Rc -eq 0 -and $r.Output -match 'RESULT=FOCUSED_PASS mode=backend coverage=FOCUSED_BACKEND manifest_sha256=NONE exit_code=0') 'filter: focused backend emits RESULT=FOCUSED_PASS and coverage=FOCUSED_BACKEND'
    Assert ((-not $r.Output.Contains('RESULT=PASS')) -and ($r.Output -notmatch 'RESULT=PASS') -and ($r.Output -notmatch 'coverage=FULL_')) 'filter: focused backend never matches RESULT=PASS substring or FULL_ coverage'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'full', '-Filter', 'FullyQualifiedName~Inventory')
    Assert ($r.Rc -eq 0 -and $r.Output -match 'RESULT=FOCUSED_PASS mode=full coverage=FOCUSED_ALL manifest_sha256=NONE exit_code=0') 'filter: focused full emits RESULT=FOCUSED_PASS and coverage=FOCUSED_ALL'
    Assert ((-not $r.Output.Contains('RESULT=PASS')) -and ($r.Output -notmatch 'coverage=FULL_ALL')) 'filter: focused full never matches RESULT=PASS substring or coverage=FULL_ALL'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'full', '-Manifest', $manifest)
    Assert ($r.Rc -eq 0 -and $r.Output -match "RESULT=PASS mode=full coverage=FULL_ALL manifest_sha256=$manifestSha exit_code=0") 'coverage: unfiltered full emits RESULT=PASS coverage=FULL_ALL with manifest sha256'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'full', '-Manifest', $manifest) @{ FAKE_RUN_RC = '19' }
    Assert ($r.Rc -eq 19 -and $r.Output -match "RESULT=FAIL mode=full coverage=NONE manifest_sha256=$manifestSha exit_code=19") 'coverage: failed full run reports coverage=NONE and exit_code=19'
    Assert (($r.Output -notmatch 'coverage=FULL_ALL') -and (-not $r.Output.Contains('RESULT=PASS'))) 'coverage: failed full run never emits FULL_ALL or RESULT=PASS'

    $reviewedDockerfileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $env:FAKE_DOCKERFILE).Hash.ToLowerInvariant()
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'frontend', '-Rebuild', '-Manifest', $manifest) @{ FAKE_EDIT_LIVE_DOCKERFILE = '1' }
    Assert ($r.Rc -eq 0 -and $r.Calls -match "BUILT_DOCKERFILE_SHA256 $reviewedDockerfileHash") 'verify-linux -Manifest: build uses reviewed recipe while live Dockerfile changes'
    Assert ($r.Output -match "RESULT=PASS mode=frontend coverage=FRONTEND manifest_sha256=$manifestSha exit_code=0") 'coverage: frontend emits coverage=FRONTEND with manifest sha256'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'frontend', '-Manifest', $manifest) @{ FAKE_STALE_TOOLS_IMAGE = '1' }
    Assert ($r.Rc -eq 0 -and $r.Calls -match "BUILT_DOCKERFILE_SHA256 $reviewedDockerfileHash") 'verify-linux -Manifest: rebuilds a cached image with mismatched recipe hash'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'frontend', '-Manifest', $manifest, '-Image', 'unverified:local')
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'verify-linux -Manifest: refuses a custom image before calling Docker'

    Write-Text (Join-Path $tmp 'keys/candidate.key') "fake`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'HEAD', '-Out', 'artifacts/m/secret.manifest')
    Assert ($r.Rc -ne 0 -and $r.Output -match 'Secret-like files') 'manifest -Write refuses secret-like paths'
    Remove-Item -LiteralPath (Join-Path $tmp 'keys') -Recurse -Force

    Write-Text $openapi "EDITED-AFTER-REVIEW`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest)
    Assert ($r.Rc -eq 1 -and $r.Output -match 'CHANGED\s+packages/api-client/openapi.json') 'verify: content edit after review detected'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api', '-Manifest', $manifest)
    Assert ($r.Rc -eq 1 -and -not $r.Calls) 'verify-linux -Manifest: mismatching candidate fails before calling Docker'
    Write-Text $openapi "FAKE-OPENAPI`n"

    Write-Text (Join-Path $tmp 'notes/extra.txt') "not reviewed`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest)
    Assert ($r.Rc -eq 1 -and $r.Output -match 'EXTRA\s+notes/extra.txt') 'verify: unreviewed extra file detected'
    Remove-Item -LiteralPath (Join-Path $tmp 'notes/extra.txt')

    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest, '-Index')
    Assert ($r.Rc -eq 1 -and $r.Output -match 'MISSING') 'index: nothing staged is a mismatch'
    Invoke-TmpGit add -- packages/api-client/openapi.json packages/api-client/src/generated/schema.ts notes/new.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest, '-Index')
    Assert ($r.Rc -eq 0 -and $r.Output -match 'CANDIDATE MATCH: staged index') 'index: staged reviewed content matches'

    Invoke-TmpGit update-index --chmod=+x -- notes/new.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest, '-Index')
    Assert ($r.Rc -eq 1 -and $r.Output -match 'CHANGED\s+notes/new.txt \(reviewed 100644 .*now 100755') 'index: mode change (chmod +x) after review detected'
    Invoke-TmpGit update-index --chmod=-x -- notes/new.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest, '-Index')
    Assert ($r.Rc -eq 0) 'index: reverting the mode restores MATCH'

    Write-Text $openapi "EDITED-AND-STAGED-AFTER-REVIEW`n"
    Invoke-TmpGit add -- packages/api-client/openapi.json
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest, '-Index')
    Assert ($r.Rc -eq 1 -and $r.Output -match 'CHANGED\s+packages/api-client/openapi.json') 'index: staged edit after review detected (same paths, same HEAD)'

    # =====================================================================
    # 3. PR-2 Docs-Only Local Verification Tests (-Mode docs)
    # =====================================================================
    Copy-ToolingScripts $docsRepo
    Write-Text (Join-Path $docsRepo '.gitignore') ".env`nartifacts/`n"
    Write-Text (Join-Path $docsRepo 'README.md') "# Root`n`nSee [Guide](docs/guide.md).`n"
    Write-Text (Join-Path $docsRepo 'docs/guide.md') "# Guide`n"
    Write-Text (Join-Path $docsRepo 'docs/obsolete.md') "# Obsolete`n"
    Write-Text (Join-Path $docsRepo 'backend/App.cs') "class A {}`n"
    Invoke-GitAt $docsRepo init -q
    Invoke-GitAt $docsRepo add -A
    Invoke-GitAt $docsRepo commit -q -m base
    $docsBase = "$(& git -C $docsRepo rev-parse HEAD)".Trim()

    # Valid candidate: modified + staged new + untracked new + deleted unlinked
    Write-Text (Join-Path $docsRepo 'docs/guide.md') "# Guide v2`n`nSee [Staged](staged.md) and [Untracked](untracked.md).`n"
    Write-Text (Join-Path $docsRepo 'docs/staged.md') "# Staged`n"
    Invoke-GitAt $docsRepo add docs/staged.md
    Write-Text (Join-Path $docsRepo 'docs/untracked.md') "# Untracked`n`nBack to [Root](../README.md).`n"
    Remove-Item -LiteralPath (Join-Path $docsRepo 'docs/obsolete.md')

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $docsBase) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $docsRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'RESULT=PASS mode=docs coverage=DOCS manifest_sha256=NONE exit_code=0') 'docs unmanifested: validates modified+staged+untracked+deleted with coverage=DOCS and manifest_sha256=NONE'
    Assert ($r.Calls -notmatch 'docker\.sock' -and $r.Calls -notmatch 'verify-role=heavy-pg') 'docs: never mounts Docker socket or labels heavy-pg'

    $docManifest = 'artifacts/docs/candidate.manifest'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', $docsBase, '-Out', $docManifest) -Root $docsRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'ENTRIES:\s+4') 'docs manifest: written for 4 doc entries'
    $docManifestSha = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $docsRepo $docManifest)).Hash.ToLowerInvariant()

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Manifest', $docManifest) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $docsRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match "RESULT=PASS mode=docs coverage=DOCS manifest_sha256=$docManifestSha exit_code=0") 'docs manifest-bound: validates exact materialized snapshot with manifest_sha256'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Manifest', $docManifest, '-Base', $docsBase) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools') } -Root $docsRepo
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'docs: -Base cannot be combined with -Manifest (exit 2)'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend', '-Base', $docsBase) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools') } -Root $docsRepo
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'docs: -Base rejected outside docs mode (exit 2)'

    # Broken link inside untracked file -> fails
    Write-Text (Join-Path $docsRepo 'docs/untracked.md') "# Broken`n`n[Missing](nope.md)`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $docsBase) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $docsRepo
    Assert ($r.Rc -ne 0 -and $r.Output -match 'RESULT=FAIL mode=docs coverage=NONE' -and $r.Output -match 'missing') 'docs: broken link in untracked Markdown rejected'
    Write-Text (Join-Path $docsRepo 'docs/untracked.md') "# Fixed`n"

    # Untracked non-doc file alongside docs -> fails closed
    Write-Text (Join-Path $docsRepo 'backend/Extra.cs') "class Extra {}`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $docsBase) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $docsRepo
    Assert ($r.Rc -ne 0 -and $r.Output -match 'RESULT=FAIL mode=docs coverage=NONE' -and $r.Output -match 'not docs-only') 'docs: untracked non-doc file rejected fail-closed'
    Remove-Item -LiteralPath (Join-Path $docsRepo 'backend/Extra.cs')

    # Secret-like file under docs/secrets/ -> refused on host before Docker (exit 4)
    Write-Text (Join-Path $docsRepo 'docs/secrets/leaked.md') "# Secret`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $docsBase) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $docsRepo
    Assert ($r.Rc -eq 4 -and -not $r.Calls) 'docs: secret-like file docs/secrets/leaked.md refused on host (exit 4) before Docker'
    Remove-Item -LiteralPath (Join-Path $docsRepo 'docs/secrets') -Recurse -Force

    # Deleting linked doc (README.md links docs/guide.md) -> fails
    Remove-Item -LiteralPath (Join-Path $docsRepo 'docs/guide.md')
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $docsBase) @{ FAKE_DOCKERFILE = (Join-Path $docsRepo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $docsRepo
    Assert ($r.Rc -ne 0 -and $r.Output -match 'RESULT=FAIL mode=docs coverage=NONE' -and $r.Output -match 'missing') 'docs: deleting linked Markdown breaks surviving link and fails'

    # =====================================================================
    # 3B. PR-2 G4 Remediation Tests: F01 (Symlink/Junction), F02 (Staged/WT), F03 (Unicode)
    # =====================================================================
    # --- F01-A: Git symlink mode 120000 under Windows core.symlinks=false ---
    Copy-ToolingScripts $f01Repo
    Write-Text (Join-Path $f01Repo '.gitignore') ".env`nartifacts/`n"
    Write-Text (Join-Path $f01Repo 'README.md') "# F01`n`nSee [Guide](docs/guide.md).`n"
    Write-Text (Join-Path $f01Repo 'docs/guide.md') "# Guide`n"
    Write-Text (Join-Path $f01Repo 'packages/api-client/openapi.json') "API`n"
    Write-Text (Join-Path $f01Repo 'packages/api-client/src/generated/schema.ts') "SCHEMA`n"
    Invoke-GitAt $f01Repo init -q
    Invoke-GitAt $f01Repo config core.symlinks false
    Invoke-GitAt $f01Repo add -A
    Invoke-GitAt $f01Repo commit -q -m base
    $f01Base = "$(& git -C $f01Repo rev-parse HEAD)".Trim()

    $symBlob = ("../outside-harmless.txt" | & git -C $f01Repo hash-object -w --stdin).Trim()
    Invoke-GitAt $f01Repo update-index --add --cacheinfo "120000,$symBlob,docs/symlink.md"
    Write-Text (Join-Path $f01Repo 'docs/symlink.md') "../outside-harmless.txt"
    $f01Manifest = 'artifacts/f01/candidate.manifest'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', $f01Base, '-Out', $f01Manifest) -Root $f01Repo
    Assert ($r.Rc -eq 0) 'F01-A: manifest records Git symlink entry mode 120000'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Manifest', $f01Manifest) @{ FAKE_DOCKERFILE = (Join-Path $f01Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f01Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'RESULT=FAIL mode=docs coverage=NONE' -and $r.Output -match '120000' -and (-not $r.Output.Contains('RESULT=PASS'))) 'F01-A: manifest-bound docs rejects mode 120000 before Docker'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f01Base) @{ FAKE_DOCKERFILE = (Join-Path $f01Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f01Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'RESULT=FAIL mode=docs coverage=NONE' -and $r.Output -match '120000' -and (-not $r.Output.Contains('RESULT=PASS'))) 'F01-A: unmanifested docs rejects mode 120000 before Docker'
    Invoke-GitAt $f01Repo rm -q -f -- docs/symlink.md

    # --- F01-B: Windows NTFS directory junction pointing outside repo (top-level, nested, and non-doc full snapshot) ---
    Write-Text (Join-Path $f01Outside 'leaked.md') "# Harmless outside fixture content`n"
    $topJunc = Join-Path $f01Repo 'docs\ext-junc'
    cmd /c mklink /J "`"$topJunc`"" "`"$f01Outside`"" 2>&1 | Out-Null
    $createdJunctions += $topJunc
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', $f01Base, '-Out', 'artifacts/f01/junc.manifest') -Root $f01Repo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'UNSAFE PATH') 'F01-B: candidate-manifest -Write refuses NTFS junction before hashing outside file'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f01Base) @{ FAKE_DOCKERFILE = (Join-Path $f01Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f01Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'RESULT=FAIL mode=docs coverage=NONE' -and $r.Output -match 'reparse point' -and (-not $r.Output.Contains('RESULT=PASS'))) 'F01-B: unmanifested docs rejects NTFS junction before copying or invoking Docker'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{ FAKE_DOCKERFILE = (Join-Path $f01Repo 'infra/Dockerfile.tools') } -Root $f01Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'RESULT=FAIL mode=backend coverage=NONE' -and $r.Output -match 'reparse point') 'F01-B: full backend snapshot enumeration rejects NTFS junction before copying or invoking Docker'
    cmd /c rmdir "`"$topJunc`"" 2>&1 | Out-Null

    # Nested NTFS junction in deep ancestor directory
    $nestedParent = Join-Path $f01Repo 'docs\nested\deep'
    New-Item -ItemType Directory -Force -Path $nestedParent | Out-Null
    $nestedJunc = Join-Path $nestedParent 'ext-junc'
    cmd /c mklink /J "`"$nestedJunc`"" "`"$f01Outside`"" 2>&1 | Out-Null
    $createdJunctions += $nestedJunc
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f01Base) @{ FAKE_DOCKERFILE = (Join-Path $f01Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f01Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'reparse point') 'F01-B: nested NTFS junction ancestor rejected before copying or Docker'
    cmd /c rmdir "`"$nestedJunc`"" 2>&1 | Out-Null
    Remove-Item -LiteralPath (Join-Path $f01Repo 'docs\nested') -Recurse -Force

    # Normal in-repository Markdown still passes cleanly
    Write-Text (Join-Path $f01Repo 'docs/guide.md') "# Clean Guide`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f01Base) @{ FAKE_DOCKERFILE = (Join-Path $f01Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f01Repo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'RESULT=PASS mode=docs coverage=DOCS') 'F01: normal in-repository Markdown accepted after unsafe links removed'

    # --- F02: Staged vs working-tree consistency in verify-linux.ps1 -Mode docs ---
    Copy-ToolingScripts $f02Repo
    Write-Text (Join-Path $f02Repo '.gitignore') ".env`nartifacts/`n"
    Write-Text (Join-Path $f02Repo 'README.md') "# F02`n`nSee [Guide](docs/guide.md).`n"
    Write-Text (Join-Path $f02Repo 'docs/guide.md') "# Guide`n"
    Write-Text (Join-Path $f02Repo 'docs/obsolete.md') "# Obsolete`n"
    Invoke-GitAt $f02Repo init -q
    Invoke-GitAt $f02Repo add -A
    Invoke-GitAt $f02Repo commit -q -m base
    $f02Base = "$(& git -C $f02Repo rev-parse HEAD)".Trim()

    # Exact G4 repro: staged broken Markdown + working tree reverted to valid base -> REJECTED before Docker
    Write-Text (Join-Path $f02Repo 'docs/guide.md') "# Broken`n`n[Missing](nope.md)`n"
    Invoke-GitAt $f02Repo add docs/guide.md
    Write-Text (Join-Path $f02Repo 'docs/guide.md') "# Guide`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'RESULT=FAIL mode=docs coverage=NONE' -and $r.Output -match 'staged index differs from working tree') 'F02: staged broken + reverted working tree rejected before Docker'

    # Staged broken + edited working tree to different valid content -> REJECTED
    Write-Text (Join-Path $f02Repo 'docs/guide.md') "# Different Valid`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'staged index differs from working tree') 'F02: staged broken + edited valid working tree rejected'

    # Staged valid + edited working tree to broken -> REJECTED
    Write-Text (Join-Path $f02Repo 'docs/guide.md') "# Valid Staged`n"
    Invoke-GitAt $f02Repo add docs/guide.md
    Write-Text (Join-Path $f02Repo 'docs/guide.md') "# Broken WT`n`n[Missing](nope.md)`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'staged index differs from working tree') 'F02: staged valid + broken working tree rejected'
    Invoke-GitAt $f02Repo checkout HEAD -- docs/guide.md

    # Staged addition + modified/deleted on disk -> REJECTED
    Write-Text (Join-Path $f02Repo 'docs/staged-new.md') "# Added v1`n"
    Invoke-GitAt $f02Repo add docs/staged-new.md
    Write-Text (Join-Path $f02Repo 'docs/staged-new.md') "# Added v2`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'staged index differs from working tree') 'F02: staged addition modified on disk rejected'
    Remove-Item -LiteralPath (Join-Path $f02Repo 'docs/staged-new.md')
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'staged index differs from working tree') 'F02: staged addition deleted on disk rejected'
    Invoke-GitAt $f02Repo reset -q HEAD -- docs/staged-new.md

    # Staged deletion + recreated on disk -> REJECTED
    Invoke-GitAt $f02Repo rm -q docs/obsolete.md
    Write-Text (Join-Path $f02Repo 'docs/obsolete.md') "# Recreated`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'staged deletion') 'F02: staged deletion recreated on disk rejected'
    Invoke-GitAt $f02Repo checkout HEAD -- docs/obsolete.md

    # Staged rename (clean PASS, post-edit REJECTED)
    Invoke-GitAt $f02Repo mv docs/obsolete.md docs/renamed.md
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'RESULT=PASS mode=docs coverage=DOCS') 'F02: clean staged rename (add+delete) accepted'
    Write-Text (Join-Path $f02Repo 'docs/renamed.md') "# Edited after mv`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 1 -and -not $r.Calls -and $r.Output -match 'staged index differs from working tree') 'F02: staged rename edited on disk rejected'
    Invoke-GitAt $f02Repo reset -q HEAD
    Remove-Item -LiteralPath (Join-Path $f02Repo 'docs/renamed.md') -ErrorAction SilentlyContinue
    Invoke-GitAt $f02Repo checkout HEAD -- docs/obsolete.md

    # Non-overlapping staged (matching disk) + separate unstaged + separate untracked -> PASS
    Write-Text (Join-Path $f02Repo 'docs/guide.md') "# Staged Matching`n"
    Invoke-GitAt $f02Repo add docs/guide.md
    Write-Text (Join-Path $f02Repo 'docs/obsolete.md') "# Unstaged Separate`n"
    Write-Text (Join-Path $f02Repo 'docs/untracked-separate.md') "# Untracked Separate`n"
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', $f02Base) @{ FAKE_DOCKERFILE = (Join-Path $f02Repo 'infra/Dockerfile.tools'); FAKE_RUN_DOCS_PYTHON = '1' } -Root $f02Repo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'RESULT=PASS mode=docs coverage=DOCS') 'F02: non-overlapping staged + unstaged + untracked Markdown accepted'

    # --- F03: Vietnamese Unicode filenames with spaces under Windows PS 5.1 CP437, CP1252, and UTF-8 ---
    Copy-ToolingScripts $f03Repo
    $uniOld = "docs/t" + [char]0x00E0 + "i li" + [char]0x1EC7 + "u c" + [char]0x0169 + ".md"
    $uniNew = "docs/h" + [char]0x01B0 + [char]0x1EDB + "ng d" + [char]0x1EAB + "n tr" + [char]0x00E0 + ".md"
    Write-Text (Join-Path $f03Repo '.gitignore') ".env`nartifacts/`n"
    Write-Text (Join-Path $f03Repo 'README.md') "# F03`n`nSee [Guide](docs/guide.md).`n"
    Write-Text (Join-Path $f03Repo 'docs/guide.md') "# Guide`n"
    Write-Text (Join-Path $f03Repo $uniOld) "# Old Vietnamese Doc`n"
    Invoke-GitAt $f03Repo init -q
    Invoke-GitAt $f03Repo add -A
    Invoke-GitAt $f03Repo commit -q -m base
    $f03Base = "$(& git -C $f03Repo rev-parse HEAD)".Trim()

    $uniContent = "# H" + [char]0x01B0 + [char]0x1EDB + "ng d" + [char]0x1EAB + "n tr" + [char]0x00E0 + "`n`nXem [Root](../README.md).`n"
    Write-Text (Join-Path $f03Repo $uniNew) $uniContent
    Remove-Item -LiteralPath (Join-Path $f03Repo $uniOld)
    $expectedUniSha = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $f03Repo $uniNew)).Hash.ToLowerInvariant()

    foreach ($cp in 437, 1252, 65001) {
        Remove-Item -LiteralPath $fakeLog -ErrorAction SilentlyContinue
        $f03Runner = Join-Path $bin "f03-cp$cp.ps1"
        $f03Script = @"
`$env:FAKE_DOCKERFILE = '$f03Repo\infra\Dockerfile.tools'
`$env:FAKE_RUN_DOCS_PYTHON = '1'
[Console]::InputEncoding = [Text.Encoding]::GetEncoding($cp)
[Console]::OutputEncoding = [Text.Encoding]::GetEncoding($cp)
`$in0 = [Console]::InputEncoding.CodePage
`$enc0 = [Console]::OutputEncoding.CodePage
& '$f03Repo\scripts\candidate-manifest.ps1' -Write -Base '$f03Base' -Out 'artifacts/f03/candidate-$cp.manifest'
`$rcW = `$LASTEXITCODE
`$in1 = [Console]::InputEncoding.CodePage
`$enc1 = [Console]::OutputEncoding.CodePage
& '$f03Repo\scripts\candidate-manifest.ps1' -Verify 'artifacts/f03/candidate-$cp.manifest'
`$rcV = `$LASTEXITCODE
`$in2 = [Console]::InputEncoding.CodePage
`$enc2 = [Console]::OutputEncoding.CodePage
& '$f03Repo\scripts\verify-linux.ps1' -Mode docs -Manifest 'artifacts/f03/candidate-$cp.manifest'
`$rcM = `$LASTEXITCODE
`$in3 = [Console]::InputEncoding.CodePage
`$enc3 = [Console]::OutputEncoding.CodePage
& '$f03Repo\scripts\verify-linux.ps1' -Mode docs -Base '$f03Base'
`$rcU = `$LASTEXITCODE
`$in4 = [Console]::InputEncoding.CodePage
`$enc4 = [Console]::OutputEncoding.CodePage
Write-Output "F03_SUMMARY cp=$cp rcW=`$rcW rcV=`$rcV rcM=`$rcM rcU=`$rcU enc=`$enc0,`$enc1,`$enc2,`$enc3,`$enc4 in=`$in0,`$in1,`$in2,`$in3,`$in4"
"@
        Write-Text $f03Runner $f03Script
        $env:FAKE_CHECK_FILE = $uniNew
        try {
            $f03Out = & powershell -NoProfile -ExecutionPolicy Bypass -File $f03Runner 2>&1 | Out-String
        } finally {
            Remove-Item env:FAKE_CHECK_FILE -ErrorAction SilentlyContinue
        }
        $f03Calls = if (Test-Path -LiteralPath $fakeLog) { Get-Content -LiteralPath $fakeLog -Raw } else { '' }
        $script:last = [pscustomobject]@{ Rc = 0; Output = $f03Out; Calls = $f03Calls }
        Assert ($f03Out -match "F03_SUMMARY cp=$cp rcW=0 rcV=0 rcM=0 rcU=0 enc=$cp,$cp,$cp,$cp,$cp in=$cp,$cp,$cp,$cp,$cp") "F03 (CP$cp): Vietnamese Unicode add+delete passes manifest write/verify/manifest-docs/unmanifested-docs and preserves console input/output encoding"
        Assert ($f03Calls -match 'CHECK_FILE_EXISTS True' -and $f03Calls -match "CHECK_FILE_SHA256 $expectedUniSha") "F03 (CP$cp): materialized snapshot contains exact Vietnamese Unicode file and SHA-256 content"
    }

    # =====================================================================
    # 4. PR-2 Candidate Manifest Hardening (-Both, -RequireBase, -PublishMode)
    # =====================================================================
    Copy-ToolingScripts $topoRepo
    Write-Text (Join-Path $topoRepo '.gitignore') "artifacts/`n"
    Write-Text (Join-Path $topoRepo 'file.txt') "m1`n"
    Write-Text (Join-Path $topoRepo 'shared.txt') "base`n"
    Invoke-GitAt $topoRepo init -q -b main
    Invoke-GitAt $topoRepo add -A
    Invoke-GitAt $topoRepo commit -q -m m1
    $m1 = "$(& git -C $topoRepo rev-parse HEAD)".Trim()

    # Create an unrelated feature branch F1 from M1
    Invoke-GitAt $topoRepo checkout -q -b feature/other $m1
    Write-Text (Join-Path $topoRepo 'other.txt') "other`n"
    Invoke-GitAt $topoRepo add other.txt
    Invoke-GitAt $topoRepo commit -q -m other-feature
    $f1 = "$(& git -C $topoRepo rev-parse HEAD)".Trim()

    # Advance main to M2
    Invoke-GitAt $topoRepo checkout -q main
    Write-Text (Join-Path $topoRepo 'main-only.txt') "m2`n"
    Invoke-GitAt $topoRepo add main-only.txt
    Invoke-GitAt $topoRepo commit -q -m m2
    $m2 = "$(& git -C $topoRepo rev-parse HEAD)".Trim()

    # Task branch starting at M1 (simulating stale base before main moved to M2)
    Invoke-GitAt $topoRepo checkout -q -b feature/task $m1
    Write-Text (Join-Path $topoRepo 'file.txt') "candidate-v1`n"
    Write-Text (Join-Path $topoRepo 'added.txt') "new-file`n"
    $mTopo = 'artifacts/t/candidate.manifest'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', $m1, '-Out', $mTopo) -Root $topoRepo
    Assert ($r.Rc -eq 0) 'topo: write manifest at M1'

    # -Both validation: mutually exclusive with -Index
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Index', '-Both') -Root $topoRepo
    Assert ($r.Rc -eq 2) 'manifest: -Index + -Both rejected with exit 2'

    # Working tree matches, staged index empty -> -Both fails on [staged-index]
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match '\[staged-index\] MISSING') 'manifest -Both: working tree match + unstaged index fails'

    # Stage exact manifest paths -> -Both matches!
    Invoke-GitAt $topoRepo add -- file.txt added.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both') -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'CANDIDATE MATCH: working tree and staged index') 'manifest -Both: both working tree and staged index match'

    # Index matches, working tree edited -> -Both fails on [working-tree]
    Write-Text (Join-Path $topoRepo 'file.txt') "drift-wt`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match '\[working-tree\] CHANGED') 'manifest -Both: index match + working tree edit fails'
    Write-Text (Join-Path $topoRepo 'file.txt') "candidate-v1`n"

    # Extra untracked file -> -Both fails on [working-tree] EXTRA
    Write-Text (Join-Path $topoRepo 'extra-untracked.txt') "unreviewed`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match '\[working-tree\] EXTRA\s+extra-untracked\.txt') 'manifest -Both: extra untracked file detected'
    Remove-Item -LiteralPath (Join-Path $topoRepo 'extra-untracked.txt')

    # Mode mismatch in index -> -Both fails
    Invoke-GitAt $topoRepo update-index --chmod=+x -- added.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'CHANGED\s+added\.txt') 'manifest -Both: mode mismatch detected'
    Invoke-GitAt $topoRepo update-index --chmod=-x -- added.txt

    # -RequireBase: decoupled from default -Verify
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo) -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'CANDIDATE MATCH') 'manifest: default -Verify still matches when main moved (integrity decoupled from freshness)'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-RequireBase', 'main') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'BASE MISMATCH') 'manifest -RequireBase: detects stale base vs main (M1 != M2)'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-RequireBase', $m1) -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'CANDIDATE MATCH') 'manifest -RequireBase: passes when base matches expected SHA'

    # -PublishMode initial on fresh branch (HEAD == M1 == manifest base)
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', $m1, '-PublishMode', 'initial') -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'publish-mode initial') 'topology initial: fresh branch at base passes'

    # -PublishMode republish on fresh branch (HEAD == base) -> rejected!
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'TOPOLOGY MISMATCH \(republish\)') 'topology republish: rejects HEAD == base'

    # -PublishedHead rejected in initial mode
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-PublishMode', 'initial', '-PublishedHead', $m1) -Root $topoRepo
    Assert ($r.Rc -eq 2) 'topology: -PublishedHead rejected in initial mode (exit 2)'

    # Initial publish after base-integration (task-integrate onto M2 -> HEAD == M2)
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'topo-init', '-Action', 'start', '-Onto', 'main') -Root $topoRepo
    Assert ($r.Rc -eq 0) 'topology initial post-integration: task-integrate onto M2 succeeds'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    Assert ($r.Rc -eq 0) 'topology initial post-integration: manifest rewritten at M2'
    Invoke-GitAt $topoRepo add -- file.txt added.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'initial') -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'publish-mode initial') 'topology initial: after base-integration (HEAD == M2) passes'

    # Commit initial publish -> C1 (published branch!). Now initial mode MUST fail, republish with remediation MUST pass!
    Invoke-GitAt $topoRepo commit -q -m 'feat: initial publish'
    $c1 = "$(& git -C $topoRepo rev-parse HEAD)".Trim()
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'initial') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'TOPOLOGY MISMATCH \(initial\)') 'topology initial: rejects branch with existing commit (HEAD != base)'

    # CI remediation edit on published branch C1
    Write-Text (Join-Path $topoRepo 'file.txt') "candidate-v2-remediated`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    Invoke-GitAt $topoRepo add -- file.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'republish', '-PublishedHead', $c1) -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'publish-mode republish') 'topology republish: CI remediation on published branch passes'

    # Sequential commit C2 on published branch
    Invoke-GitAt $topoRepo commit -q -m 'fix: ci remediation'
    $c2 = "$(& git -C $topoRepo rev-parse HEAD)".Trim()
    Write-Text (Join-Path $topoRepo 'file.txt') "candidate-v3`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    Invoke-GitAt $topoRepo add -- file.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'republish', '-PublishedHead', $c1) -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'publish-mode republish') 'topology republish: sequential commits with valid PublishedHead pass'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'republish', '-PublishedHead', $f1) -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'TOPOLOGY MISMATCH \(republish\)') 'topology republish: non-ancestor PublishedHead rejected'
    Invoke-GitAt $topoRepo commit -q -m 'fix: follow-up'

    # Advance main to M3 (touching shared.txt AND m3.txt) and merge --no-ff --no-commit into published branch
    Invoke-GitAt $topoRepo checkout -q main
    Write-Text (Join-Path $topoRepo 'm3.txt') "m3`n"
    Invoke-GitAt $topoRepo add m3.txt
    Invoke-GitAt $topoRepo commit -q -m m3
    $m3 = "$(& git -C $topoRepo rev-parse HEAD)".Trim()

    Invoke-GitAt $topoRepo checkout -q feature/task
    # Without MERGE_HEAD, manifest base=M3 is not yet ancestor of HEAD -> republish MUST reject!
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'TOPOLOGY MISMATCH \(republish\)') 'topology republish: diverged branch without MERGE_HEAD rejected'

    # Now perform valid base-integration merge (--no-ff --no-commit main)
    Invoke-GitAt $topoRepo merge --no-ff --no-commit main
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'publish-mode republish') 'topology republish: valid MERGE_HEAD == base passes'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'initial') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'TOPOLOGY MISMATCH \(initial\)') 'topology initial: rejects MERGE_HEAD'

    # Commit the merge of M3, then do another remediation edit (published branch already integrated main)
    Invoke-GitAt $topoRepo commit -q -m 'merge: integrate M3'
    Write-Text (Join-Path $topoRepo 'file.txt') "candidate-v4-after-merge`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    Invoke-GitAt $topoRepo add -- file.txt
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-RequireBase', 'main', '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 0 -and $r.Output -match 'publish-mode republish') 'topology republish: published branch with prior base-merge commit passes'
    Invoke-GitAt $topoRepo commit -q -m 'fix: post-merge remediation'

    # Unresolved merge conflict state (main modifies shared.txt differently than branch)
    Invoke-GitAt $topoRepo checkout -q main
    Write-Text (Join-Path $topoRepo 'shared.txt') "main-conflict`n"
    Invoke-GitAt $topoRepo add shared.txt
    Invoke-GitAt $topoRepo commit -q -m m4-conflict
    $m4 = "$(& git -C $topoRepo rev-parse HEAD)".Trim()

    Invoke-GitAt $topoRepo checkout -q feature/task
    Write-Text (Join-Path $topoRepo 'shared.txt') "branch-conflict`n"
    Invoke-GitAt $topoRepo add shared.txt
    Invoke-GitAt $topoRepo commit -q -m branch-conflict
    & git -C $topoRepo merge --no-ff --no-commit main 2>&1 | Out-Null   # intentionally creates conflict
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-Both', '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'unresolved merge conflicts') 'topology: unresolved merge conflicts rejected'
    Invoke-GitAt $topoRepo merge --abort

    # Wrong MERGE_HEAD (merging unrelated branch F1 instead of main M4)
    Invoke-GitAt $topoRepo merge --no-ff --no-commit $f1
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'TOPOLOGY MISMATCH \(republish\): MERGE_HEAD') 'topology republish: MERGE_HEAD != manifest base rejected'
    # Commit that unexpected non-base merge, then merge main -> branch history has unexpected non-base merge parent F1!
    Invoke-GitAt $topoRepo commit -q -m 'bad: merge unrelated feature'
    Invoke-GitAt $topoRepo checkout -q main
    Write-Text (Join-Path $topoRepo 'm5.txt') "m5`n"
    Invoke-GitAt $topoRepo add m5.txt
    Invoke-GitAt $topoRepo commit -q -m m5
    Invoke-GitAt $topoRepo checkout -q feature/task
    Invoke-GitAt $topoRepo merge --no-ff --no-commit -X ours main
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', 'main', '-Out', $mTopo) -Root $topoRepo
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'merges non-base parent') 'topology republish: unexpected non-base merge parent in branch history rejected'
    Invoke-GitAt $topoRepo merge --abort

    # Detached HEAD rejected in publish mode
    Invoke-GitAt $topoRepo checkout -q --detach HEAD
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'detached HEAD') 'topology: detached HEAD rejected'

    # MERGE_HEAD when HEAD is already an ancestor of base (branch has 0 published commits) -> rejected!
    Invoke-GitAt $topoRepo checkout -q -b feature/no-commits $m1
    Invoke-GitAt $topoRepo merge --no-ff --no-commit $m2
    Write-Text (Join-Path $topoRepo 'dummy.txt') "x`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', $m2, '-Out', $mTopo) -Root $topoRepo
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'already an ancestor of base') 'topology republish: MERGE_HEAD with 0 branch commits rejected'
    Invoke-GitAt $topoRepo merge --abort

    # Local task-integrate WIP commit on branch -> rejected in republish mode!
    Invoke-GitAt $topoRepo checkout -q -b feature/wip-leak $m2
    Write-Text (Join-Path $topoRepo 'wip.txt') "wip`n"
    Invoke-GitAt $topoRepo add wip.txt
    Invoke-GitAt $topoRepo commit -q -m 'task-integrate WIP leaked (local only, never push)'
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Write', '-Base', $m2, '-Out', $mTopo) -Root $topoRepo
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $mTopo, '-PublishMode', 'republish') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'task-integrate WIP') 'topology republish: local task-integrate WIP commit rejected'

    # Malformed manifest entry (path traversal) -> rejected in -Verify!
    $badManifest = Join-Path $topoRepo 'artifacts/t/bad.manifest'
    Write-Text $badManifest "# candidate-manifest v2 base=$m2`n100644 $('a' * 40) ../escape.txt`n"
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', 'artifacts/t/bad.manifest') -Root $topoRepo
    Assert ($r.Rc -eq 1 -and $r.Output -match 'MANIFEST ERROR: Invalid or duplicate manifest entry') 'manifest: path traversal entry rejected on -Verify'

    # =====================================================================
    # 5. PR-2 Host-Level PostgreSQL Mutual Exclusion (Real Multi-Process Tests)
    # =====================================================================
    Copy-ToolingScripts $lockRepo
    Write-Text (Join-Path $lockRepo '.gitignore') "artifacts/`n"
    Write-Text (Join-Path $lockRepo 'README.md') "# Lock Test`n"
    Write-Text (Join-Path $lockRepo 'docs/guide.md') "# Guide`n"
    Write-Text (Join-Path $lockRepo 'packages/api-client/openapi.json') "API`n"
    Write-Text (Join-Path $lockRepo 'packages/api-client/src/generated/schema.ts') "SCHEMA`n"
    Invoke-GitAt $lockRepo init -q -b main
    Invoke-GitAt $lockRepo add -A
    Invoke-GitAt $lockRepo commit -q -m base
    Invoke-GitAt $lockRepo worktree add -q -b wt-a $lockWt1 HEAD
    Invoke-GitAt $lockRepo worktree add -q -b wt-b $lockWt2 HEAD

    # Invalid LockWaitSeconds arguments
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend', '-LockWaitSeconds', '-1') @{ FAKE_DOCKERFILE = (Join-Path $lockWt1 'infra/Dockerfile.tools') } -Root $lockWt1
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'lock: negative -LockWaitSeconds rejected with exit 2'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api', '-LockWaitSeconds', '5') @{ FAKE_DOCKERFILE = (Join-Path $lockWt1 'infra/Dockerfile.tools') } -Root $lockWt1
    Assert ($r.Rc -eq 2 -and -not $r.Calls) 'lock: -LockWaitSeconds > 0 rejected for api mode (exit 2)'

    # Filesystem / permission error (lock path points to a directory) -> exit 7 (distinct from contention exit 5)
    $dirAsLock = Join-Path $bin 'lock-is-directory'
    New-Item -ItemType Directory -Force -Path $dirAsLock | Out-Null
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{
        FAKE_DOCKERFILE          = (Join-Path $lockWt1 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH  = $dirAsLock
    } -Root $lockWt1
    Assert ($r.Rc -eq 7 -and -not $r.Calls -and $r.Output -match 'directory') 'lock: filesystem/directory error exits 7 (distinct from contention exit 5)'

    # Real 2-process contention across TWO separate worktrees sharing ONE host lock file!
    $sharedLock = Join-Path $bin 'shared-multiprocess.lock'
    $startedA = Join-Path $bin 'hold-a.started'
    $releaseA = Join-Path $bin 'hold-a.release'
    $logA = Join-Path $bin 'docker-a.log'
    $logB = Join-Path $bin 'docker-b.log'
    Remove-Item -LiteralPath $startedA, $releaseA, $logA, $logB -ErrorAction SilentlyContinue

    $procA = Start-BackgroundTool -RootDir $lockWt1 -Script 'verify-linux.ps1' -Arguments @('-Mode', 'backend') -EnvOverrides @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt1 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_HOLD_FILE          = $startedA
        FAKE_RELEASE_FILE       = $releaseA
        FAKE_LOG_FILE           = $logA
    }
    $bgProcs += $procA

    $waitDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $startedA) -and [DateTime]::UtcNow -lt $waitDeadline) {
        Start-Sleep -Milliseconds 100
    }
    Assert (Test-Path -LiteralPath $startedA) 'lock: Process A acquired host lock and entered heavy verification'

    # Process B from worktree 2 fails fast with exit 5 + holder diagnostics + never runs Docker!
    $rB = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_LOG_FILE           = $logB
    } -Root $lockWt2
    $callsB = if (Test-Path -LiteralPath $logB) { Get-Content -LiteralPath $logB -Raw } else { '' }
    Assert ($rB.Rc -eq 5 -and -not $callsB) 'lock: Process B fails fast with exit 5 and never invokes Docker'
    Assert ($rB.Output -match 'host-lock: CONTENTION' -and $rB.Output -match "pid=$($procA.Process.Id)" -and $rB.Output -match 'mode=backend' -and $rB.Output -match 'branch=wt-a') 'lock: contention output reports holder PID, mode, and branch'
    Assert ($rB.Output -notmatch 'PASSWORD|SECRET|TOKEN') 'lock: holder diagnostics contain no credentials'

    # Process B2 with bounded timeout (-LockWaitSeconds 1) waits and fails with exit 5
    $swTimeout = [Diagnostics.Stopwatch]::StartNew()
    $rB2 = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'full', '-LockWaitSeconds', '1') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_LOG_FILE           = $logB
    } -Root $lockWt2
    $swTimeout.Stop()
    Assert ($rB2.Rc -eq 5 -and $swTimeout.Elapsed.TotalMilliseconds -ge 900) 'lock: -LockWaitSeconds 1 waits and times out with exit 5'

    # Non-heavy modes (api, frontend, docs) are NOT blocked while Process A holds heavy PG lock!
    $rApi = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_OUTPUTS            = '1'
    } -Root $lockWt2
    Assert ($rApi.Rc -eq 0 -and $rApi.Output -match 'RESULT=PASS mode=api coverage=API') 'lock: api mode not blocked while heavy PG lock is held'

    $rFe = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'frontend') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
    } -Root $lockWt2
    Assert ($rFe.Rc -eq 0 -and $rFe.Output -match 'RESULT=PASS mode=frontend coverage=FRONTEND') 'lock: frontend mode not blocked while heavy PG lock is held'

    Write-Text (Join-Path $lockWt2 'docs/guide.md') "# Guide updated during lock`n"
    $rDocs = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'docs', '-Base', 'HEAD') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_RUN_DOCS_PYTHON    = '1'
    } -Root $lockWt2
    Assert ($rDocs.Rc -eq 0 -and $rDocs.Output -match 'RESULT=PASS mode=docs coverage=DOCS') 'lock: docs mode not blocked while heavy PG lock is held'

    # Start Process C waiting up to 10s, then release Process A -> Process C acquires lock & succeeds!
    $logC = Join-Path $bin 'docker-c.log'
    Remove-Item -LiteralPath $logC -ErrorAction SilentlyContinue
    $procC = Start-BackgroundTool -RootDir $lockWt2 -Script 'verify-linux.ps1' -Arguments @('-Mode', 'backend', '-LockWaitSeconds', '10') -EnvOverrides @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_LOG_FILE           = $logC
    }
    $bgProcs += $procC
    Start-Sleep -Milliseconds 500
    Write-Text $releaseA 'release'
    $procA.Process.WaitForExit(15000) | Out-Null
    $outA = $procA.OutTask.GetAwaiter().GetResult()
    $procC.Process.WaitForExit(15000) | Out-Null
    $outC = $procC.OutTask.GetAwaiter().GetResult()
    Assert ($procA.Process.ExitCode -eq 0 -and $outA -match 'host-lock: RELEASED') 'lock: Process A releases lock cleanly on success'
    Assert ($procC.Process.ExitCode -eq 0 -and $outC -match 'RESULT=PASS mode=backend coverage=FULL_BACKEND') 'lock: waiting Process C acquires lock after Process A finishes'
    Assert (Test-Path -LiteralPath $sharedLock -PathType Leaf) 'lock: lock file is preserved (not deleted) after release to avoid identity races'

    # Lock release after failure
    $rFail = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt1 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_RUN_RC             = '23'
    } -Root $lockWt1
    Assert ($rFail.Rc -eq 23 -and $rFail.Output -match 'host-lock: RELEASED') 'lock: released after container failure (exit 23)'
    $rAfterFail = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
    } -Root $lockWt2
    Assert ($rAfterFail.Rc -eq 0) 'lock: subsequent run succeeds after failure release'

    # Lock release after forceful process termination (crash) + stale metadata detection
    $startedD = Join-Path $bin 'hold-d.started'
    $releaseD = Join-Path $bin 'hold-d.release'
    Remove-Item -LiteralPath $startedD, $releaseD -ErrorAction SilentlyContinue
    $procD = Start-BackgroundTool -RootDir $lockWt1 -Script 'verify-linux.ps1' -Arguments @('-Mode', 'backend') -EnvOverrides @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt1 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
        FAKE_HOLD_FILE          = $startedD
        FAKE_RELEASE_FILE       = $releaseD
    }
    $bgProcs += $procD
    $waitDeadline2 = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $startedD) -and [DateTime]::UtcNow -lt $waitDeadline2) {
        Start-Sleep -Milliseconds 100
    }
    Assert (Test-Path -LiteralPath $startedD) 'lock crash: Process D holds lock before forced termination'
    Stop-Process -Id $procD.Process.Id -Force -ErrorAction SilentlyContinue
    $procD.Process.WaitForExit(10000) | Out-Null

    # Residual Docker workload still running after Process D crash -> Process E detects it safely (exit 6, never kills container)
    $rRes = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{
        FAKE_DOCKERFILE           = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH   = $sharedLock
        FAKE_RESIDUAL_CONTAINERS  = 'deadbeef1234 tra-hien-verify-crashed Up 2 minutes'
    } -Root $lockWt2
    Assert ($rRes.Rc -eq 6 -and $rRes.Output -match 'STALE_METADATA_DETECTED' -and $rRes.Output -match 'RESIDUAL_WORKLOAD_DETECTED') 'lock crash+residual: detects stale metadata and surviving container (exit 6)'
    Assert ($rRes.Calls -notmatch 'ARGS run' -and $rRes.Calls -notmatch 'ARGS (rm|kill|stop)') 'lock crash+residual: never starts overlapping run and never kills/removes another container'

    # Once residual workload is gone, Process F acquires the OS-released lock cleanly and succeeds!
    $rAfterCrash = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{
        FAKE_DOCKERFILE         = (Join-Path $lockWt2 'infra/Dockerfile.tools')
        TRA_HIEN_TEST_LOCK_PATH = $sharedLock
    } -Root $lockWt2
    Assert ($rAfterCrash.Rc -eq 0 -and $rAfterCrash.Output -match 'RESULT=PASS mode=backend coverage=FULL_BACKEND') 'lock crash: succeeds cleanly once residual workload stops'

    # =====================================================================
    # 6. Existing task-integrate.ps1 self-tests
    # =====================================================================
    $wtA = "$tmp-wtA"; $wtB = "$tmp-wtB"; $wtN = "$tmp-wtN"
    Invoke-TmpGit worktree add -q -b newbase $wtN HEAD
    Write-Text (Join-Path $wtN 'incoming.txt') "incoming from main`n"
    Invoke-GitAt $wtN add incoming.txt
    Invoke-GitAt $wtN commit -q -m incoming
    $newbase = "$(& git -C $tmp rev-parse newbase)".Trim()
    Invoke-TmpGit worktree add -q -b task-a $wtA HEAD
    Invoke-TmpGit worktree add -q -b task-b $wtB HEAD
    Write-Text (Join-Path $wtB 'b-stash.txt') "unrelated stash of worktree B`n"
    Invoke-GitAt $wtB stash push -q -u -m b-entry
    $stashBefore = @(& git -C $tmp stash list --format='%H') -join ','

    Write-Text (Join-Path $wtA 'packages/api-client/openapi.json') "A-CANDIDATE`n"
    Write-Text (Join-Path $wtA 'a-new.txt') "new file of A`n"
    Write-Text (Join-Path $wtA 'a-executable.sh') "exit 0`n"
    Invoke-GitAt $wtA config core.filemode false
    Invoke-GitAt $wtA add -- a-executable.sh
    Invoke-GitAt $wtA update-index --chmod=+x -- scripts/verify-linux.sh a-executable.sh
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-a', '-Action', 'start', '-Onto', 'newbase') -Root $wtA
    Assert ($r.Rc -eq 0 -and $r.Output -match 'INTEGRATED: task-a') 'integrate: A rebased onto the new base'
    Assert ("$(& git -C $wtA rev-parse HEAD)".Trim() -eq $newbase) 'integrate: A HEAD is the new base (WIP undone)'
    Assert ((Test-Path (Join-Path $wtA 'incoming.txt')) -and ([IO.File]::ReadAllText((Join-Path $wtA 'packages/api-client/openapi.json')).Trim() -eq 'A-CANDIDATE')) 'integrate: incoming change and candidate content both present'
    Assert ("$(& git -C $wtA status --porcelain -- a-new.txt)".StartsWith('??')) 'integrate: new file is untracked again'
    Assert ("$(& git -C $wtA ls-files -s -- scripts/verify-linux.sh)".StartsWith('100755 ')) 'integrate: retains staged-only executable bit on an existing file'
    Assert ("$(& git -C $wtA ls-files -s -- a-executable.sh)".StartsWith('100755 ')) 'integrate: retains staged executable bit on a new file'
    Assert ((@(& git -C $tmp stash list --format='%H') -join ',') -eq $stashBefore) 'integrate: shared refs/stash untouched'

    Write-Text (Join-Path $wtB 'incoming.txt') "B version`n"
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-b', '-Action', 'start', '-Onto', 'newbase') -Root $wtB
    Assert ($r.Rc -eq 2 -and $r.Output -match 'CONFLICT') 'integrate: B add/add conflict stops with exit 2'
    $refA = "$(& git -C $tmp rev-parse --verify --quiet refs/task-integration/task-a)".Trim()
    $refB = "$(& git -C $tmp rev-parse --verify --quiet refs/task-integration/task-b)".Trim()
    Assert ($refA -and $refB -and $refA -ne $refB) 'integrate: per-task recovery refs are independent'
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-b', '-Action', 'finish') -Root $wtB
    Assert ($r.Rc -eq 2) 'integrate: finish refuses while conflicts remain unmerged'
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-b', '-Action', 'drop') -Root $wtB
    Assert ($r.Rc -eq 2) 'integrate: drop refuses while the rebase is unfinished'
    Write-Text (Join-Path $wtB 'incoming.txt') "resolved`n"
    Invoke-GitAt $wtB add incoming.txt
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-b', '-Action', 'finish') @{ GIT_EDITOR = 'false' } -Root $wtB
    Assert ($r.Rc -eq 0 -and "$(& git -C $wtB rev-parse HEAD)".Trim() -eq $newbase -and [IO.File]::ReadAllText((Join-Path $wtB 'incoming.txt')).Trim() -eq 'resolved') 'integrate: B finishes after conflict resolution even when GIT_EDITOR=false'

    & git -C $tmp update-ref refs/task-integration/task-a $newbase 2>&1 | Out-Null   # someone moved A's ref
    $stateA = Join-Path $wtA 'artifacts/task-a/pre-integration/integration.txt'
    $originalState = [IO.File]::ReadAllText($stateA)
    foreach ($invalidWip in '', 'wip=abc') {
        Write-Text $stateA ($originalState -replace '(?m)^wip=[^\r\n]+', $invalidWip)
        $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-a', '-Action', 'drop') -Root $wtA
        $currentA = "$(& git -C $tmp rev-parse --verify --quiet refs/task-integration/task-a)".Trim()
        Assert ($r.Rc -ne 0 -and $currentA -eq $newbase -and (Test-Path -LiteralPath $stateA)) 'integrate: drop preserves ref and state when WIP SHA is absent or malformed'
        & git -C $tmp update-ref refs/task-integration/task-a $newbase 2>&1 | Out-Null
    }
    Write-Text $stateA $originalState
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-a', '-Action', 'drop') -Root $wtA
    Assert ($r.Rc -ne 0 -and "$(& git -C $tmp rev-parse refs/task-integration/task-a)".Trim() -eq $newbase) 'integrate: drop refuses (ref kept) when the ref no longer matches'
    & git -C $tmp update-ref refs/task-integration/task-a $refA 2>&1 | Out-Null
    $r = Invoke-Tool 'task-integrate.ps1' @('-Task', 'task-a', '-Action', 'drop') -Root $wtA
    $leftA = @(& git -C $tmp for-each-ref --format='%(refname)' refs/task-integration/task-a).Count
    $leftB = "$(& git -C $tmp rev-parse --verify --quiet refs/task-integration/task-b)".Trim()
    Assert ($r.Rc -eq 0 -and $leftA -eq 0 -and $leftB -eq $refB) 'integrate: drop removes only A recovery ref (compare-and-delete)'
    foreach ($w in $wtA, $wtB, $wtN) { & git -C $tmp worktree remove --force $w 2>&1 | Out-Null }
    foreach ($w in $lockWt1, $lockWt2) { & git -C $lockRepo worktree remove --force $w 2>&1 | Out-Null }
} finally {
    foreach ($bg in $bgProcs) {
        try { if (-not $bg.Process.HasExited) { Stop-Process -Id $bg.Process.Id -Force -ErrorAction SilentlyContinue } } catch {}
        try { $bg.Process.Dispose() } catch {}
    }
    $env:PATH = $originalPath
    $env:FAKE_DOCKERFILE = $originalDockerfile
    if ($null -eq $originalTestLock) { Remove-Item env:TRA_HIEN_TEST_LOCK_PATH -ErrorAction SilentlyContinue }
    else { $env:TRA_HIEN_TEST_LOCK_PATH = $originalTestLock }
    foreach ($j in $createdJunctions) {
        if (Test-Path -LiteralPath $j) { cmd /c rmdir "`"$j`"" 2>&1 | Out-Null }
    }
    foreach ($w in "$tmp-wtA", "$tmp-wtB", "$tmp-wtN", $lockWt1, $lockWt2, $docsRepo, $f01Repo, $f01Outside, $f02Repo, $f03Repo, $topoRepo, $lockRepo) {
        Remove-Item -LiteralPath $w -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $bin -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "test-agent-tooling: $script:passes passed, $script:failures failed"
exit $script:failures
