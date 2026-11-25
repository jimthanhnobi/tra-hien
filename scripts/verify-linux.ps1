<#
.SYNOPSIS
  CI-equivalent verification of the current worktree in Linux Docker (shared by Codex and Antigravity).
.DESCRIPTION
  Windows Application Control blocks the OpenAPI generator (0x800711C7), so contract generation and the
  CI jobs run in an image built from infra/Dockerfile.tools.

  Isolation:
  - Only a filtered host-side snapshot reaches the container: git-tracked files plus untracked files that
    are not git-ignored (git ls-files -co --exclude-standard). Ignored files such as infra/.env, artifacts/,
    bin/obj and node_modules are never copied or mounted. Secret-like names (.env*, *.pem, *.key, *.pfx,
    *.p12, *.secret, secrets/) are refused on the host before copying, even when force-added to the index.
    The snapshot is mounted read-only and deleted after the run.
  - The Docker socket (needed by Testcontainers and the dev-migrate tests) is mounted only for the
    backend/full modes; api/frontend/docs run without it. Mounting the socket gives the container control
    of the local Docker engine, so run this only on the developer machine with trusted repository code.
  - The image is built from infra/Dockerfile.tools in the snapshot and runs by immutable image ID.
    Manifest runs require the image's recipe hash label to match the verified Dockerfile.

  Verification integrity & host-level mutual exclusion:
  - Filtered runs (-Filter) are strictly refused with -Manifest and outside backend/full modes. Focused
    runs emit RESULT=FOCUSED_PASS with coverage=FOCUSED_BACKEND or FOCUSED_ALL (never RESULT=PASS or
    FULL_*), preventing filtered tests from being mistaken for full candidate verification evidence.
  - Heavy PostgreSQL verification modes (backend, full) acquire an OS-backed host-level file lock shared
    across all worktrees on the same host (fail-fast by default; optional -LockWaitSeconds) and check for
    residual labelled heavy verification containers before starting Docker. Modes api, frontend, and docs
    do not acquire the heavy PostgreSQL lock.
  - Limitations: worktrees invoking older unhardened scripts without this lock, workloads launched outside
    verify-linux.ps1, and processes in different OS user/session/Docker-host scopes are not automatically
    coordinated by this file lock.

  Logs, TRX and migrations.sql go to artifacts/verify-linux/<timestamp>-<mode>/ (git-ignored). Sandboxed
  agents must run this script with sandbox bypass/approval because it talks to the Docker engine.
.EXAMPLE
  ./scripts/verify-linux.ps1
  Same as the CI backend job: build, format, all backend tests, zero-skip check, migrations, check:api.
.EXAMPLE
  ./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Inventory"
  Focused tests during G3 (emits RESULT=FOCUSED_PASS coverage=FOCUSED_BACKEND). A full run is still required before READY_FOR_LOCAL_G4.
.EXAMPLE
  ./scripts/verify-linux.ps1 -Mode api -WriteBack
  Regenerate packages/api-client/openapi.json and src/generated/schema.ts into this worktree.
.EXAMPLE
  ./scripts/verify-linux.ps1 -Mode full
  Backend job plus frontend job (lint, typecheck, tests, builds).
.EXAMPLE
  ./scripts/verify-linux.ps1 -Mode docs -Manifest artifacts/my-doc-task/candidate.manifest
  Fail-closed docs-only candidate validation bound to the verified manifest (no Docker socket, no PG).
.EXAMPLE
  ./scripts/verify-linux.ps1 -Manifest artifacts/p1-10/candidate.manifest
  Final G3 run bound to the candidate: fails if the worktree differs from the manifest, then builds the
  snapshot from git objects whose IDs equal the manifest (base tree + reviewed entries), so the container
  runs exactly the fingerprinted candidate; the log records the manifest SHA-256.
#>
[CmdletBinding()]
param(
    [ValidateSet('backend', 'api', 'frontend', 'full', 'docs')][string]$Mode = 'backend',
    [AllowEmptyString()][AllowNull()][string]$Filter = '',
    [switch]$WriteBack,
    [switch]$Rebuild,
    [AllowEmptyString()][AllowNull()][string]$Image = '',
    [AllowEmptyString()][AllowNull()][string]$Manifest = '',
    [AllowEmptyString()][AllowNull()][string]$Base = '',
    [int]$LockWaitSeconds = 0
)

# Native tools report failures through exit codes; stderr must not abort Windows PowerShell 5.1.
$ErrorActionPreference = 'Continue'
$Mode = $Mode.ToLowerInvariant()   # ValidateSet is case-insensitive; the Linux script is not.
$root = Split-Path -Parent $PSScriptRoot
$canonicalRoot = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Fail-InvalidArgs([string]$Reason) {
    Write-Error $Reason
    Write-Output "verify-linux RESULT=FAIL mode=$Mode coverage=NONE manifest_sha256=NONE exit_code=2 reason=$Reason"
    exit 2
}

if ($WriteBack -and $Mode -ne 'api') {
    Fail-InvalidArgs '-WriteBack is only valid with -Mode api.'
}
if ($PSBoundParameters.ContainsKey('Manifest') -and $Manifest.Length -gt 0 -and [string]::IsNullOrWhiteSpace($Manifest)) {
    Fail-InvalidArgs '-Manifest cannot be whitespace-only.'
}
$effectiveManifest = if ([string]::IsNullOrEmpty($Manifest)) { '' } else { $Manifest.Trim() }
if ($effectiveManifest -and $Image) {
    Fail-InvalidArgs '-Image cannot be combined with -Manifest. Use the candidate tools image.'
}
if ($Filter.Length -gt 0 -and [string]::IsNullOrWhiteSpace($Filter)) {
    Fail-InvalidArgs '-Filter cannot be whitespace-only; omit -Filter or pass an empty string for an unfiltered run.'
}
$effectiveFilter = if ([string]::IsNullOrEmpty($Filter)) { '' } else { $Filter.Trim() }
$hasFilter = -not [string]::IsNullOrEmpty($effectiveFilter)
if ($effectiveManifest -and $hasFilter) {
    Fail-InvalidArgs '-Manifest cannot be combined with a non-empty -Filter; filtered tests cannot be used as full candidate verification evidence.'
}
if ($hasFilter -and ($Mode -notin 'backend', 'full')) {
    Fail-InvalidArgs "-Filter is only valid with -Mode backend or full (not -Mode $Mode)."
}
if ($PSBoundParameters.ContainsKey('Base') -and $Base.Length -gt 0 -and [string]::IsNullOrWhiteSpace($Base)) {
    Fail-InvalidArgs '-Base cannot be whitespace-only.'
}
$effectiveBase = if ([string]::IsNullOrEmpty($Base)) { '' } else { $Base.Trim() }
if ($effectiveBase -and $Mode -ne 'docs') {
    Fail-InvalidArgs '-Base is only valid with -Mode docs (without -Manifest).'
}
if ($effectiveBase -and $effectiveManifest) {
    Fail-InvalidArgs '-Base cannot be combined with -Manifest (the manifest header defines the base SHA).'
}
if ($LockWaitSeconds -lt 0 -or $LockWaitSeconds -gt 3600) {
    Fail-InvalidArgs '-LockWaitSeconds must be between 0 and 3600.'
}
if ($LockWaitSeconds -gt 0 -and ($Mode -notin 'backend', 'full')) {
    Fail-InvalidArgs "-LockWaitSeconds is only valid with -Mode backend or full (not -Mode $Mode)."
}

$plannedCoverage = switch ($Mode) {
    'backend'  { if ($hasFilter) { 'FOCUSED_BACKEND' } else { 'FULL_BACKEND' } }
    'full'     { if ($hasFilter) { 'FOCUSED_ALL' } else { 'FULL_ALL' } }
    'api'      { 'API' }
    'frontend' { 'FRONTEND' }
    'docs'     { 'DOCS' }
}
$requiresPgLock = ($Mode -in 'backend', 'full')

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$out = Join-Path $root "artifacts/verify-linux/$stamp-$Mode"
$snapshot = Join-Path $root "artifacts/verify-linux/.snapshots/$stamp-$Mode"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'verify.log'

$script:manifestField = 'NONE'
$script:lockStream = $null
$script:lockPath = ''
$script:lockScopeHash = ''
$script:runId = "$stamp-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
$script:containerName = "tra-hien-verify-$($script:runId)"

function Write-Log([string]$Message) { $Message | Tee-Object -FilePath $log -Append }

function Release-HeavyPgLock {
    if ($null -ne $script:lockStream) {
        try {
            $script:lockStream.Seek(0, [IO.SeekOrigin]::Begin) | Out-Null
            $script:lockStream.SetLength(0)
            $script:lockStream.Flush($true)
        } catch {
            # Best-effort metadata clear; always dispose handle below.
        }
        try { $script:lockStream.Dispose() } catch {}
        $script:lockStream = $null
        Write-Log "host-lock: RELEASED lock_path=$($script:lockPath) scope=$($script:lockScopeHash) run_id=$($script:runId)"
    }
}

function Cleanup-Snapshot {
    if (Test-Path -LiteralPath $snapshot) {
        Remove-Item -LiteralPath $snapshot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Stop-Run([string]$Message, [int]$Code) {
    Release-HeavyPgLock
    Cleanup-Snapshot
    Write-Log "verify-linux RESULT=FAIL mode=$Mode coverage=NONE manifest_sha256=$($script:manifestField) exit_code=$Code reason=$Message log=$log"
    exit $Code
}

function Get-StringSha256Hex([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $utf8NoBom.GetBytes($Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Invoke-GitBytes([string[]]$GitArgs) {
    $psi = [Diagnostics.ProcessStartInfo]::new('git')
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $escaped = @($GitArgs | ForEach-Object {
        if ($_ -eq '') { '""' }
        elseif ($_ -match '[\s"]') { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
        else { $_ }
    })
    $psi.Arguments = $escaped -join ' '
    $proc = [Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $proc.Start() | Out-Null
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $ms = [IO.MemoryStream]::new()
    try {
        $proc.StandardOutput.BaseStream.CopyTo($ms)
        $proc.WaitForExit()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            ExitCode = $proc.ExitCode
            Bytes    = $ms.ToArray()
            Stderr   = $stderr
        }
    } finally {
        $ms.Dispose()
        $proc.Dispose()
    }
}

function Parse-GitZPaths([byte[]]$Bytes) {
    if ($Bytes.Length -eq 0) { return @() }
    if ($Bytes[$Bytes.Length - 1] -ne 0) { Stop-Run 'incomplete NUL-delimited git path output' 1 }
    $text = $utf8NoBom.GetString($Bytes, 0, $Bytes.Length - 1)
    if ($text.Length -eq 0) { return @() }
    return @($text.Split([char]0) | Where-Object { $_ -ne '' })
}

function Parse-GitZNameStatus([byte[]]$Bytes) {
    if ($Bytes.Length -eq 0) { return @() }
    if ($Bytes[$Bytes.Length - 1] -ne 0) { Stop-Run 'incomplete NUL-delimited git diff output' 1 }
    $text = $utf8NoBom.GetString($Bytes, 0, $Bytes.Length - 1)
    if ($text.Length -eq 0) { return @() }
    $tokens = $text.Split([char]0)
    if (($tokens.Count % 2) -ne 0) { Stop-Run 'malformed git diff status/path pairs' 1 }
    $items = @()
    for ($i = 0; $i -lt $tokens.Count; $i += 2) {
        $items += [pscustomobject]@{ Status = $tokens[$i]; Path = $tokens[$i + 1] }
    }
    return $items
}

function Parse-GitZRawDiff([byte[]]$Bytes) {
    $map = [ordered]@{}
    if ($Bytes.Length -eq 0) { return $map }
    if ($Bytes[$Bytes.Length - 1] -ne 0) { Stop-Run 'incomplete NUL-delimited git raw diff output' 1 }
    $text = $utf8NoBom.GetString($Bytes, 0, $Bytes.Length - 1)
    if ($text.Length -eq 0) { return $map }
    $tokens = $text.Split([char]0)
    if (($tokens.Count % 2) -ne 0) { Stop-Run 'malformed git raw diff metadata/path pairs' 1 }
    for ($i = 0; $i -lt $tokens.Count; $i += 2) {
        $meta = $tokens[$i]
        $rel = $tokens[$i + 1]
        if ($meta -cnotmatch '^:([0-7]{6}) ([0-7]{6}) [0-9a-f]+ [0-9a-f]+ ([A-Z][0-9]*)$') {
            Stop-Run "malformed git raw diff entry: $meta" 1
        }
        $map[$rel] = [pscustomobject]@{
            OldMode = $Matches[1]
            NewMode = $Matches[2]
            Status  = $Matches[3]
            Path    = $rel
        }
    }
    return $map
}

function Parse-GitZStageEntries([byte[]]$Bytes) {
    $entries = @()
    foreach ($line in @(Parse-GitZPaths $Bytes)) {
        $tab = $line.IndexOf("`t")
        if ($tab -le 0) { Stop-Run "malformed git ls-files -s entry: $line" 1 }
        $meta = $line.Substring(0, $tab) -split ' '
        if ($meta.Count -lt 3) { Stop-Run "malformed git ls-files -s metadata: $line" 1 }
        $entries += [pscustomobject]@{
            Mode  = $meta[0]
            Blob  = $meta[1]
            Stage = $meta[2]
            Path  = $line.Substring($tab + 1)
        }
    }
    return $entries
}

function Invoke-GitHashObjects([string[]]$Paths, [switch]$WriteObjects, [switch]$NoFilters) {
    $allHashes = @()
    if ($Paths.Count -eq 0) {
        return [pscustomobject]@{ ExitCode = 0; Hashes = $allHashes; Stderr = '' }
    }
    $prefixArgs = @('hash-object')
    if ($WriteObjects) { $prefixArgs += '-w' }
    if ($NoFilters) { $prefixArgs += '--no-filters' }
    $prefixArgs += '--'
    # Pass paths via UTF-16 CreateProcessW argv (batched under Windows' 32k command-line limit)
    # instead of RedirectStandardInput, because .NET Framework 4.x Process.Start() writes a
    # UTF-8 BOM to redirected stdin whenever Console.InputEncoding is CP65001 with preamble.
    $maxCmdChars = 16000
    $batch = New-Object 'System.Collections.Generic.List[string]'
    $batchChars = 0
    for ($i = 0; $i -le $Paths.Count; $i++) {
        $atEnd = ($i -eq $Paths.Count)
        $nextLen = if (-not $atEnd) { $Paths[$i].Length + 4 } else { 0 }
        if ($batch.Count -gt 0 -and ($atEnd -or ($batchChars + $nextLen) -gt $maxCmdChars)) {
            $res = Invoke-GitBytes ($prefixArgs + $batch.ToArray())
            if ($res.ExitCode -ne 0) {
                return [pscustomobject]@{ ExitCode = $res.ExitCode; Hashes = @(); Stderr = $res.Stderr }
            }
            $stdout = $utf8NoBom.GetString($res.Bytes)
            $allHashes += @($stdout -split "\r?\n" | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim() })
            $batch.Clear()
            $batchChars = 0
        }
        if (-not $atEnd) {
            [void]$batch.Add($Paths[$i])
            $batchChars += $nextLen
        }
    }
    return [pscustomobject]@{
        ExitCode = 0
        Hashes   = $allHashes
        Stderr   = ''
    }
}

function Test-DiskEntryExists([string]$FullPath) {
    try {
        [void][IO.File]::GetAttributes($FullPath)
        return $true
    } catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
        return $false
    } catch {
        return $true
    }
}

function Assert-SafeRepoPathsOnDisk([string[]]$RelPaths) {
    $safeDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $rootPrefix = $canonicalRoot + [IO.Path]::DirectorySeparatorChar
    foreach ($rel in $RelPaths) {
        if (-not (Test-SafeRepoRelPath $rel)) {
            Stop-Run "unsafe or invalid repository relative path: $rel" 1
        }
        $parts = $rel -split '/'
        $current = $canonicalRoot
        for ($i = 0; $i -lt $parts.Count; $i++) {
            $current = Join-Path $current $parts[$i]
            $isLeaf = ($i -eq ($parts.Count - 1))
            if (-not $isLeaf -and $safeDirs.Contains($current)) { continue }
            try {
                $attrs = [IO.File]::GetAttributes($current)
            } catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
                break
            } catch {
                Stop-Run "cannot inspect filesystem attributes of '$current' for '$rel': $($_.Exception.Message)" 1
            }
            if (($attrs -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-Run "unsafe symlink, junction, or reparse point at '$current' for path '$rel'" 1
            }
            if (-not $isLeaf) { [void]$safeDirs.Add($current) }
        }
        $fullTarget = [IO.Path]::GetFullPath((Join-Path $canonicalRoot $rel))
        if (-not $fullTarget.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Run "path '$rel' escapes repository root '$canonicalRoot'" 1
        }
    }
}

function Assert-NoDisallowedDocsGitModes($DiffMap, [string]$Context) {
    foreach ($key in $DiffMap.Keys) {
        $item = $DiffMap[$key]
        if ($item.OldMode -eq '120000' -or $item.NewMode -eq '120000') {
            Stop-Run "Git symlink entry (mode 120000) is not allowed for docs-only verification ($Context): $($item.Path)" 1
        }
        if ($item.OldMode -eq '160000' -or $item.NewMode -eq '160000') {
            Stop-Run "Gitlink/submodule entry (mode 160000) is not allowed for docs-only verification ($Context): $($item.Path)" 1
        }
    }
}

function Resolve-CommitSha([string]$Ref) {
    $resolved = @(& git -C $root rev-parse --verify --quiet "$Ref^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $resolved.Count -eq 0) { return '' }
    $sha = "$($resolved[0])".Trim()
    if ($sha -cnotmatch '^[0-9a-f]{40,64}$') { return '' }
    return $sha
}

function Assert-NoUnmergedIndex {
    $uRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-u', '-z')
    if ($uRes.ExitCode -ne 0) { Stop-Run 'git ls-files -u failed' 1 }
    if ($uRes.Bytes.Length -gt 0) { Stop-Run 'unresolved merge conflicts exist in the index' 1 }
}

function Test-SecretPath([string]$Path) {
    $normalized = $Path -replace '\\', '/'
    $leaf = ($normalized -split '/')[-1]
    if ($leaf -match '^\.env(\..+)?$' -and $leaf -notmatch '\.example$') { return $true }
    if ($leaf -match '\.(pfx|p12|pem|key|secret)$') { return $true }
    return ($normalized -match '(^|/)secrets/')
}

function Test-SafeRepoRelPath([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) { return $false }
    if ($Path.Contains('\') -or $Path.StartsWith('/') -or $Path -match '^[A-Za-z]:') { return $false }
    if ($Path -match '[\x00-\x1F\x7F]') { return $false }
    foreach ($part in ($Path -split '/')) {
        if ($part -eq '' -or $part -eq '.' -or $part -eq '..') { return $false }
    }
    return $true
}

function Write-DocsCandidateInventory([string]$DocsBaseSha, [object[]]$ChangeItems) {
    if ($ChangeItems.Count -eq 0) {
        Stop-Run "no candidate changes relative to base $DocsBaseSha; empty diff is not a valid docs-only candidate" 1
    }
    $snapRootFull = [IO.Path]::GetFullPath($snapshot).TrimEnd('\', '/')
    $snapRelPaths = @(
        Get-ChildItem -LiteralPath $snapshot -Recurse -File -Force | ForEach-Object {
            $_.FullName.Substring($snapRootFull.Length + 1) -replace '\\', '/'
        }
    )
    [Array]::Sort($snapRelPaths, [StringComparer]::Ordinal)
    $pathsPayload = if ($snapRelPaths.Count -gt 0) { ($snapRelPaths -join "`n") + "`n" } else { '' }
    $snapPathsHash = Get-StringSha256Hex $pathsPayload

    $serializedChanges = @()
    foreach ($item in $ChangeItems) {
        $st = "$($item.Status)"
        $rel = "$($item.Path)"
        $gitMode = if ($null -ne $item.Mode -and "$($item.Mode)" -ne '') { "$($item.Mode)" } else { $null }
        if (-not (Test-SafeRepoRelPath $rel)) {
            Stop-Run "unsafe or invalid candidate path for docs validation: $rel" 1
        }
        if ($st -notin 'A', 'M', 'D') {
            Stop-Run "unsupported change status '$st' on '$rel' for docs-only validation" 1
        }
        if ($st -in 'A', 'M') {
            if ($gitMode -eq '120000') {
                Stop-Run "Git symlink entry (mode 120000) is not allowed for docs-only verification: $rel" 1
            }
            if ($gitMode -notin '100644', '100755') {
                Stop-Run "unsupported Git mode '$gitMode' on '$rel' for docs-only validation" 1
            }
            $snapFile = Join-Path $snapshot $rel
            if (-not (Test-Path -LiteralPath $snapFile -PathType Leaf)) {
                Stop-Run "candidate file missing from materialized snapshot: $rel" 1
            }
            try {
                $fileSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $snapFile -ErrorAction Stop).Hash.ToLowerInvariant()
            } catch {
                Stop-Run "cannot hash candidate file in snapshot ($rel): $($_.Exception.Message)" 1
            }
            $serializedChanges += [ordered]@{
                status = $st
                path   = $rel
                sha256 = $fileSha
                mode   = $gitMode
            }
        } else {
            $serializedChanges += [ordered]@{
                status = 'D'
                path   = $rel
                sha256 = $null
                mode   = $null
            }
        }
    }

    $invObj = [ordered]@{
        schema                = 'local-candidate-v1'
        base                  = $DocsBaseSha
        manifest_sha256       = $script:manifestField
        snapshot_file_count   = $snapRelPaths.Count
        snapshot_paths_sha256 = $snapPathsHash
        changes               = @($serializedChanges)
    }
    $invPath = Join-Path $out 'candidate-inventory.json'
    [IO.File]::WriteAllText($invPath, ($invObj | ConvertTo-Json -Depth 5), $utf8NoBom)
    Write-Log "docs-inventory: base=$DocsBaseSha changes=$($serializedChanges.Count) snapshot_files=$($snapRelPaths.Count) manifest_sha256=$($script:manifestField) file=$invPath"
}

function Read-LockHolderSummary([string]$Path) {
    try {
        $raw = ''
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            $rs = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try {
                $sr = [IO.StreamReader]::new($rs, $utf8NoBom)
                $raw = $sr.ReadToEnd().Trim()
            } finally {
                $rs.Dispose()
            }
            if ($raw) { break }
            Start-Sleep -Milliseconds 50
        }
        if (-not $raw) { return 'unknown (holder metadata not yet written)' }
        $obj = ConvertFrom-Json -InputObject $raw -ErrorAction Stop
        return "pid=$($obj.pid) mode=$($obj.mode) run_id=$($obj.run_id) container=$($obj.container_name) branch=$($obj.branch) worktree=$($obj.worktree) started_utc=$($obj.started_utc)"
    } catch {
        return "unavailable ($($_.Exception.Message))"
    }
}

function Acquire-HeavyPgLock([string]$BranchName) {
    $lockOverride = $env:TRA_HIEN_TEST_LOCK_PATH
    if (-not [string]::IsNullOrWhiteSpace($lockOverride)) {
        $script:lockPath = [IO.Path]::GetFullPath($lockOverride.Trim())
        Write-Log "host-lock: lock-override=TEST_ONLY path=$($script:lockPath)"
    } else {
        $lockDir = Join-Path ([IO.Path]::GetTempPath()) 'tra-hien-locks'
        $script:lockPath = [IO.Path]::GetFullPath((Join-Path $lockDir 'heavy-pg-verify.lock'))
    }
    $script:lockScopeHash = (Get-StringSha256Hex $script:lockPath.ToLowerInvariant()).Substring(0, 16)

    try {
        $lockParent = Split-Path -Parent $script:lockPath
        if ($lockParent) { [IO.Directory]::CreateDirectory($lockParent) | Out-Null }
        if (Test-Path -LiteralPath $script:lockPath -PathType Container) {
            Stop-Run "host lock path is a directory, not a file: $($script:lockPath)" 7
        }
    } catch {
        Stop-Run "host lock filesystem/permission error preparing '$($script:lockPath)': $($_.Exception.Message)" 7
    }

    $waitWatch = [Diagnostics.Stopwatch]::StartNew()
    $stream = $null
    while ($null -eq $stream) {
        try {
            $stream = [IO.FileStream]::new(
                $script:lockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::Read
            )
        } catch {
            $ex = $_.Exception
            while ($null -ne $ex.InnerException -and $ex -is [Management.Automation.MethodInvocationException]) {
                $ex = $ex.InnerException
            }
            $isContention = ($ex -is [IO.IOException]) -and ($ex.HResult -in @(-2147024864, -2147024863))
            if (-not $isContention) {
                Stop-Run "host lock filesystem/permission error on '$($script:lockPath)': $($ex.Message)" 7
            }
            if ($LockWaitSeconds -gt 0 -and $waitWatch.Elapsed.TotalSeconds -lt $LockWaitSeconds) {
                Start-Sleep -Milliseconds 250
                continue
            }
            $holderDesc = Read-LockHolderSummary $script:lockPath
            $waited = [int]$waitWatch.Elapsed.TotalSeconds
            Write-Log "host-lock: CONTENTION lock_path=$($script:lockPath) scope=$($script:lockScopeHash) wait_seconds=$LockWaitSeconds elapsed_wait=${waited}s holder=$holderDesc"
            Stop-Run "heavy PostgreSQL verification lock is already held ($holderDesc)" 5
        }
    }

    $script:lockStream = $stream
    $staleRaw = ''
    if ($stream.Length -gt 0) {
        $stream.Seek(0, [IO.SeekOrigin]::Begin) | Out-Null
        $buf = New-Object byte[] $stream.Length
        $bytesRead = $stream.Read($buf, 0, $buf.Length)
        if ($bytesRead -gt 0) { $staleRaw = ($utf8NoBom.GetString($buf, 0, $bytesRead)).Trim() }
    }
    if ($staleRaw) {
        Write-Log "host-lock: STALE_METADATA_DETECTED previous_holder=$staleRaw"
    }

    $holderObj = [ordered]@{
        pid             = $PID
        mode            = $Mode
        run_id          = $script:runId
        container_name  = $script:containerName
        lock_scope_hash = $script:lockScopeHash
        worktree        = $root
        branch          = $BranchName
        started_utc     = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    $holderBytes = $utf8NoBom.GetBytes(($holderObj | ConvertTo-Json -Compress))
    $stream.Seek(0, [IO.SeekOrigin]::Begin) | Out-Null
    $stream.SetLength(0)
    $stream.Write($holderBytes, 0, $holderBytes.Length)
    $stream.Flush($true)
    Write-Log "host-lock: ACQUIRED lock_path=$($script:lockPath) scope=$($script:lockScopeHash) pid=$PID run_id=$($script:runId) container=$($script:containerName)"

    # Guard against a surviving heavy Docker verification container left behind after an ungraceful process termination.
    $residualRaw = @(& docker ps --filter "label=org.tra-hien.verify-lock=$($script:lockScopeHash)" --format '{{.ID}} {{.Names}} {{.Status}}' 2>&1)
    $psRc = $LASTEXITCODE
    if ($psRc -ne 0) {
        Stop-Run "docker ps residual-workload check failed (exit $psRc): $($residualRaw -join ' ')" 1
    }
    $activeResidual = @($residualRaw | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    if ($activeResidual.Count -gt 0) {
        Write-Log "host-lock: RESIDUAL_WORKLOAD_DETECTED scope=$($script:lockScopeHash) containers=$($activeResidual -join '; ')"
        Write-Log "host-lock: refusing to start overlapping heavy verification and refusing to stop/remove another run's container."
        Stop-Run "residual heavy verification container still running ($($activeResidual -join '; '))" 6
    }
}

# 1. Filtered snapshot: tracked + untracked-not-ignored files only.
    $lsRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-co', '--exclude-standard', '--deduplicate', '-z')
    if ($lsRes.ExitCode -ne 0) { Stop-Run 'git ls-files failed' 1 }
    $paths = @(Parse-GitZPaths $lsRes.Bytes)
    if ($paths.Count -eq 0) { Stop-Run 'git ls-files returned empty repository file list' 1 }

    # Reject any filesystem symlink, Windows NTFS junction, or reparse point BEFORE hashing/reading/copying any target bytes.
    Assert-SafeRepoPathsOnDisk -RelPaths $paths

    # Secret-like names (the .gitignore secret patterns) are refused on the host BEFORE anything is copied,
    # including files force-added to the index. infra/.env.example and *.env.*.example remain allowed.
    $secretPaths = @($paths | Where-Object { (Test-SecretPath $_) -and (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) })
    if ($secretPaths.Count -gt 0) { Stop-Run "secret-like files would enter the container (remove them from the index/worktree): $($secretPaths -join ', ')" 4 }

    $copied = 0
    $snapshotSource = 'worktree copy (tracked + untracked-not-ignored)'
    $docsBaseSha = ''
    $docsChanges = @()

    if ($effectiveManifest) {
        # Read the manifest file ONCE into memory and compute its SHA-256 from those exact bytes so a
        # concurrent edit to the manifest file cannot cause a mismatch between the logged digest and the
        # materialized snapshot. Build the snapshot from immutable git objects (read-tree + cacheinfo +
        # checkout-index), then verify the materialized snapshot directory on disk before logging MATCH.
        $manifestFile = if ([IO.Path]::IsPathRooted($effectiveManifest)) { $effectiveManifest } else { Join-Path $root $effectiveManifest }
        if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { Stop-Run "manifest not found: $manifestFile" 1 }
        $manifestBytes = [IO.File]::ReadAllBytes($manifestFile)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $manifestDigest = ([BitConverter]::ToString($sha256.ComputeHash($manifestBytes)) -replace '-', '').ToLowerInvariant() }
        finally { $sha256.Dispose() }
        $script:manifestField = $manifestDigest
        $manifestHeader = '# candidate-manifest v2 base='
        $lines = @(([Text.Encoding]::UTF8.GetString($manifestBytes) -split "\r?\n") | Where-Object { $_ -ne '' })
        if ($lines.Count -lt 2 -or -not $lines[0].StartsWith($manifestHeader) -or
            $lines[0].Substring($manifestHeader.Length) -cnotmatch '^[0-9a-f]{40,64}$') {
            Stop-Run 'invalid or outdated manifest header (expected candidate-manifest v2)' 1
        }
        $baseSha = $lines[0].Substring($manifestHeader.Length)
        $entries = @()
        $expectedMap = @{}
        foreach ($line in $lines[1..($lines.Count - 1)]) {
            $parts = $line.Split(' ', 3)
            if ($parts.Count -ne 3 -or -not (Test-SafeRepoRelPath $parts[2]) -or $expectedMap.ContainsKey($parts[2])) {
                Stop-Run "invalid manifest entry: $line" 1
            }
            if ($parts[1] -eq 'DELETED') {
                if ($parts[0] -ne '-') { Stop-Run "invalid DELETED manifest entry mode: $line" 1 }
            } else {
                if ($parts[0] -cnotmatch '^(100644|100755|120000)$' -or $parts[1] -cnotmatch '^[0-9a-f]{40,64}$') {
                    Stop-Run "invalid manifest entry mode/blob: $line" 1
                }
                if (Test-SecretPath $parts[2]) {
                    Stop-Run "secret-like file listed in manifest: $($parts[2])" 4
                }
            }
            $expectedMap[$parts[2]] = "$($parts[0]) $($parts[1])"
            $entries += [pscustomobject]@{ Mode = $parts[0]; Blob = $parts[1]; Path = $parts[2] }
        }

        Assert-NoUnmergedIndex

        if ($Mode -eq 'docs') {
            foreach ($entry in $entries) {
                if ($entry.Mode -eq '120000') {
                    Stop-Run "Git symlink entry (mode 120000) in manifest is not allowed for docs-only verification: $($entry.Path)" 1
                }
            }
        }

        # Verify the working tree against the in-memory manifest while writing candidate blobs into .git/objects.
        $wtTrackedRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'diff', '--name-only', '--no-renames', '-z', $baseSha)
        if ($wtTrackedRes.ExitCode -ne 0) { Stop-Run "git diff $baseSha failed" 1 }
        $wtTracked = @(Parse-GitZPaths $wtTrackedRes.Bytes)
        $wtUntrackedRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-o', '--exclude-standard', '-z')
        if ($wtUntrackedRes.ExitCode -ne 0) { Stop-Run 'git ls-files -o failed' 1 }
        $wtUntracked = @(Parse-GitZPaths $wtUntrackedRes.Bytes)
        $idxRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-s', '-z')
        if ($idxRes.ExitCode -ne 0) { Stop-Run 'git ls-files -s failed' 1 }
        $indexModes = @{}
        foreach ($idxEntry in @(Parse-GitZStageEntries $idxRes.Bytes)) {
            $indexModes[$idxEntry.Path] = $idxEntry.Mode
        }
        $wtPaths = @($wtTracked + $wtUntracked | Where-Object { $_ } | Sort-Object -Unique)
        if ($wtPaths.Count -gt 0) { Assert-SafeRepoPathsOnDisk -RelPaths $wtPaths }
        $wtPresent = @($wtPaths | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
        $actualMap = @{}
        if ($wtPresent.Count -gt 0) {
            $hashRes = Invoke-GitHashObjects -Paths $wtPresent -WriteObjects
            if ($hashRes.ExitCode -ne 0 -or $hashRes.Hashes.Count -ne $wtPresent.Count) { Stop-Run 'git hash-object -w failed' 1 }
            for ($i = 0; $i -lt $wtPresent.Count; $i++) {
                $p = $wtPresent[$i]
                $m = if ($indexModes.ContainsKey($p)) { $indexModes[$p] } else { '100644' }
                $actualMap[$p] = "$m $($hashRes.Hashes[$i])"
            }
        }
        foreach ($p in $wtPaths) { if (-not $actualMap.ContainsKey($p)) { $actualMap[$p] = '- DELETED' } }
        $mismatches = @()
        foreach ($p in $expectedMap.Keys) {
            if (-not $actualMap.ContainsKey($p)) { $mismatches += "MISSING $p" }
            elseif ($actualMap[$p] -ne $expectedMap[$p]) { $mismatches += "CHANGED $p (reviewed $($expectedMap[$p]), now $($actualMap[$p]))" }
        }
        foreach ($p in $actualMap.Keys) { if (-not $expectedMap.ContainsKey($p)) { $mismatches += "EXTRA $p" } }
        if ($mismatches.Count -gt 0) {
            $mismatches | Sort-Object | ForEach-Object { Write-Log "manifest-mismatch: $_" }
            Stop-Run 'worktree does not match the candidate manifest' 1
        }

        # Materialize the snapshot strictly from .git/objects via a temporary index, then verify the snapshot on disk.
        $present = @($entries | Where-Object { $_.Blob -ne 'DELETED' })
        $deleted = @($entries | Where-Object { $_.Blob -eq 'DELETED' } | ForEach-Object { $_.Path })
        $tmpIndex = Join-Path $out 'snapshot.index'
        $previousIndex = $env:GIT_INDEX_FILE
        $env:GIT_INDEX_FILE = $tmpIndex
        try {
            & git -C $root read-tree $baseSha
            if ($LASTEXITCODE -ne 0) { Stop-Run "git read-tree $baseSha failed" 1 }
            for ($start = 0; $start -lt $present.Count; $start += 40) {
                $batch = @('update-index', '--add')
                foreach ($entry in $present[$start..([Math]::Min($start + 39, $present.Count - 1))]) {
                    $batch += @('--cacheinfo', "$($entry.Mode),$($entry.Blob),$($entry.Path)")
                }
                & git -C $root @batch
                if ($LASTEXITCODE -ne 0) { Stop-Run 'git update-index --cacheinfo failed' 1 }
            }
            for ($start = 0; $start -lt $deleted.Count; $start += 40) {
                $delBatch = @($deleted[$start..([Math]::Min($start + 39, $deleted.Count - 1))])
                & git -C $root update-index --force-remove -- @delBatch
                if ($LASTEXITCODE -ne 0) { Stop-Run 'git update-index --force-remove failed' 1 }
            }
            $stageRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-s', '-z')
            if ($stageRes.ExitCode -ne 0) { Stop-Run 'git ls-files -s on temporary index failed' 1 }
            $stageEntries = @(Parse-GitZStageEntries $stageRes.Bytes)
            if ($stageEntries.Count -eq 0) { Stop-Run 'git ls-files -s on temporary index returned no entries' 1 }
            $snapshotPaths = @()
            $expectedSnapshotBlobs = @()
            foreach ($sEntry in $stageEntries) {
                if ($Mode -eq 'docs' -and $sEntry.Mode -eq '120000') {
                    Stop-Run "Git symlink entry (mode 120000) in candidate tree is not allowed for docs-only verification: $($sEntry.Path)" 1
                }
                if ($Mode -eq 'docs' -and $sEntry.Mode -eq '160000') {
                    Stop-Run "Gitlink/submodule entry (mode 160000) in candidate tree is not allowed for docs-only verification: $($sEntry.Path)" 1
                }
                if (Test-SecretPath $sEntry.Path) { Stop-Run "secret-like files in the candidate: $($sEntry.Path)" 4 }
                $snapshotPaths += $sEntry.Path
                $expectedSnapshotBlobs += $sEntry.Blob
            }
            $treeOut = @(& git -C $root write-tree)
            if ($LASTEXITCODE -ne 0 -or $treeOut.Count -eq 0) { Stop-Run 'git write-tree failed' 1 }
            $snapshotTree = "$($treeOut[0])".Trim()
            $prefix = ($snapshot.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/') + '/'
            & git -c core.autocrlf=false -C $root checkout-index -a -f "--prefix=$prefix"
            if ($LASTEXITCODE -ne 0) { Stop-Run 'git checkout-index into the snapshot failed' 1 }
        } finally {
            if ($null -eq $previousIndex) { Remove-Item env:GIT_INDEX_FILE -ErrorAction SilentlyContinue } else { $env:GIT_INDEX_FILE = $previousIndex }
            Remove-Item -LiteralPath $tmpIndex -ErrorAction SilentlyContinue
        }

        # Verify the materialized snapshot directory itself BEFORE logging MATCH or mounting it into Docker.
        $matPaths = @($snapshotPaths | ForEach-Object { Join-Path $snapshot $_ })
        $matRes = Invoke-GitHashObjects -Paths $matPaths -NoFilters
        if ($matRes.ExitCode -ne 0 -or $matRes.Hashes.Count -ne $expectedSnapshotBlobs.Count) {
            Stop-Run 'post-materialization snapshot hash failed' 1
        }
        for ($i = 0; $i -lt $expectedSnapshotBlobs.Count; $i++) {
            if ($matRes.Hashes[$i] -ne $expectedSnapshotBlobs[$i]) {
                Stop-Run "snapshot tampering or mismatch on $($snapshotPaths[$i])" 1
            }
        }
        $diskCount = @(Get-ChildItem -LiteralPath $snapshot -Recurse -File -Force).Count
        if ($diskCount -ne $snapshotPaths.Count) {
            Stop-Run "snapshot file count mismatch ($diskCount on disk vs $($snapshotPaths.Count) in tree)" 1
        }
        $copied = $snapshotPaths.Count
        $snapshotSource = "manifest objects (tree $snapshotTree, base $baseSha + $($entries.Count) verified entries)"
        Write-Log "manifest-check: CANDIDATE MATCH: working tree & materialized snapshot == manifest sha256 $manifestDigest tree $snapshotTree ($($entries.Count) entries, base $baseSha)"

        if ($Mode -eq 'docs') {
            $docsBaseSha = $baseSha
            $baseTreeRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-tree', '-r', '--name-only', '-z', $baseSha)
            if ($baseTreeRes.ExitCode -ne 0) { Stop-Run "git ls-tree $baseSha failed" 1 }
            $baseTreeSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($bp in @(Parse-GitZPaths $baseTreeRes.Bytes)) { [void]$baseTreeSet.Add($bp) }
            $sortedEntries = @($entries | Sort-Object -Property Path)
            $docsChanges = @($sortedEntries | ForEach-Object {
                $st = if ($_.Blob -eq 'DELETED') { 'D' } elseif ($baseTreeSet.Contains($_.Path)) { 'M' } else { 'A' }
                $em = if ($st -eq 'D') { $null } else { $_.Mode }
                [pscustomobject]@{ Status = $st; Path = $_.Path; Mode = $em }
            })
        }
    } else {
        if ($Mode -eq 'docs') {
            Assert-NoUnmergedIndex
            $baseRef = if ($effectiveBase) { $effectiveBase } else { 'origin/main' }
            $docsBaseSha = Resolve-CommitSha $baseRef
            if (-not $docsBaseSha) {
                Stop-Run "cannot resolve base '$baseRef' to a commit; specify -Base <ref|sha> or -Manifest <path>" 1
            }
            $headCheck = Resolve-CommitSha 'HEAD'
            if (-not $headCheck) {
                Stop-Run 'cannot resolve HEAD to a commit' 1
            }

            # Inspect index modes upfront to block Git symlink (120000) / gitlink (160000) entries even under Windows core.symlinks=false.
            $idxRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-s', '-z')
            if ($idxRes.ExitCode -ne 0) { Stop-Run 'git ls-files -s failed' 1 }
            $indexModeMap = @{}
            foreach ($idxEntry in @(Parse-GitZStageEntries $idxRes.Bytes)) {
                if ($idxEntry.Stage -ne '0') { Stop-Run "unmerged index stage $($idxEntry.Stage) on $($idxEntry.Path)" 1 }
                if ($idxEntry.Mode -eq '120000') { Stop-Run "Git symlink entry (mode 120000) in index is not allowed for docs-only verification: $($idxEntry.Path)" 1 }
                if ($idxEntry.Mode -eq '160000') { Stop-Run "Gitlink/submodule entry (mode 160000) in index is not allowed for docs-only verification: $($idxEntry.Path)" 1 }
                $indexModeMap[$idxEntry.Path] = $idxEntry.Mode
            }

            $wtBaseRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'diff', '--no-ext-diff', '--no-textconv', '--no-renames', '--raw', '-z', $docsBaseSha, '--')
            if ($wtBaseRes.ExitCode -ne 0) { Stop-Run "git diff $docsBaseSha failed" 1 }
            $cachedBaseRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'diff', '--cached', '--no-ext-diff', '--no-textconv', '--no-renames', '--raw', '-z', $docsBaseSha, '--')
            if ($cachedBaseRes.ExitCode -ne 0) { Stop-Run "git diff --cached $docsBaseSha failed" 1 }
            $cachedHeadRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'diff', '--cached', '--no-ext-diff', '--no-textconv', '--no-renames', '--raw', '-z', 'HEAD', '--')
            if ($cachedHeadRes.ExitCode -ne 0) { Stop-Run 'git diff --cached HEAD failed' 1 }
            $wtIndexRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'diff', '--no-ext-diff', '--no-textconv', '--no-renames', '--raw', '-z', '--')
            if ($wtIndexRes.ExitCode -ne 0) { Stop-Run 'git diff (worktree vs index) failed' 1 }
            $untrackedRes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-o', '--exclude-standard', '-z')
            if ($untrackedRes.ExitCode -ne 0) { Stop-Run 'git ls-files -o failed' 1 }

            $wtVsBase = Parse-GitZRawDiff $wtBaseRes.Bytes
            $cachedVsBase = Parse-GitZRawDiff $cachedBaseRes.Bytes
            $cachedVsHead = Parse-GitZRawDiff $cachedHeadRes.Bytes
            $wtVsIndex = Parse-GitZRawDiff $wtIndexRes.Bytes
            $untrackedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($uPath in @(Parse-GitZPaths $untrackedRes.Bytes)) { [void]$untrackedSet.Add($uPath) }

            Assert-NoDisallowedDocsGitModes $wtVsBase 'worktree vs base'
            Assert-NoDisallowedDocsGitModes $cachedVsBase 'index vs base'
            Assert-NoDisallowedDocsGitModes $cachedVsHead 'index vs HEAD'
            Assert-NoDisallowedDocsGitModes $wtVsIndex 'worktree vs index'

            # Reject ambiguous staged-vs-working-tree divergence BEFORE copying or validating file contents.
            foreach ($rel in $cachedVsHead.Keys) {
                $cItem = $cachedVsHead[$rel]
                if ($wtVsIndex.Contains($rel)) {
                    Stop-Run "staged index differs from working tree on '$rel'; reconcile or restage before local docs verification" 1
                }
                $diskPath = Join-Path $root $rel
                if ($cItem.Status -eq 'D' -and ((Test-DiskEntryExists $diskPath) -or $untrackedSet.Contains($rel))) {
                    Stop-Run "staged deletion of '$rel' conflicts with file present in working tree" 1
                }
                if (($cItem.Status -in 'A', 'M') -and -not (Test-Path -LiteralPath $diskPath -PathType Leaf)) {
                    Stop-Run "staged change on '$rel' is not reflected as a regular file in working tree" 1
                }
            }
            foreach ($rel in $cachedVsBase.Keys) {
                $cItem = $cachedVsBase[$rel]
                if (-not $wtVsBase.Contains($rel)) {
                    Stop-Run "staged/index change relative to base on '$rel' is not reflected in working tree" 1
                }
                $diskPath = Join-Path $root $rel
                if ($cItem.Status -eq 'D' -and ((Test-DiskEntryExists $diskPath) -or $untrackedSet.Contains($rel))) {
                    Stop-Run "staged/index deletion of '$rel' conflicts with file present in working tree" 1
                }
            }

            $changeMap = @{}
            foreach ($rel in $wtVsBase.Keys) {
                $wItem = $wtVsBase[$rel]
                $em = if ($wItem.Status -eq 'D') { $null } else { $wItem.NewMode }
                $changeMap[$rel] = [pscustomobject]@{ Status = $wItem.Status; Path = $rel; Mode = $em }
            }
            foreach ($uPath in $untrackedSet) {
                if (-not $changeMap.ContainsKey($uPath)) {
                    $em = if ($indexModeMap.ContainsKey($uPath)) { $indexModeMap[$uPath] } else { '100644' }
                    $changeMap[$uPath] = [pscustomobject]@{ Status = 'A'; Path = $uPath; Mode = $em }
                }
            }
            $sortedKeys = [string[]]@($changeMap.Keys)
            [Array]::Sort($sortedKeys, [StringComparer]::Ordinal)
            $docsChanges = @($sortedKeys | ForEach-Object { $changeMap[$_] })
        }

        foreach ($relative in $paths) {
            $source = Join-Path $root $relative
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }   # tracked but deleted in the worktree
            $target = Join-Path $snapshot $relative
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            [IO.File]::Copy($source, $target, $true)
            $copied++
        }
    }

    if ($Mode -eq 'docs') {
        Write-DocsCandidateInventory -DocsBaseSha $docsBaseSha -ChangeItems $docsChanges
        if (-not $effectiveManifest) {
            Write-Log 'docs-warning: manifest_sha256=NONE (unmanifested local docs check; not manifest-bound G3/G4 evidence)'
        }
    }

    $head = "$(& git -C $root rev-parse HEAD)".Trim()
    $branch = "$(& git -C $root rev-parse --abbrev-ref HEAD)".Trim()
    $dirty = @(git -C $root status --porcelain).Count

try {
    # 2. Acquire host-level PostgreSQL mutual exclusion lock for heavy modes (backend, full) BEFORE invoking Docker.
    if ($requiresPgLock) {
        Acquire-HeavyPgLock -BranchName $branch
    }

    # 3. Resolve the tools image from the snapshot recipe, including on cache hits.
    $dockerfile = Join-Path $snapshot 'infra/Dockerfile.tools'
    try {
        $fingerprint = (Get-FileHash -Algorithm SHA256 -LiteralPath $dockerfile -ErrorAction Stop).Hash.ToLowerInvariant()
    } catch {
        Stop-Run 'cannot hash infra/Dockerfile.tools in the snapshot' 1
    }
    $imageLabel = 'org.tra-hien.tools.dockerfile-sha256'
    if (-not $Image) { $Image = "tra-hien-tools:$($fingerprint.Substring(0, 12))" }

    function Resolve-ToolsImage {
        $format = if ($effectiveManifest) { '{{.Id}} {{json .Config.Labels}}' } else { '{{.Id}}' }
        $inspect = @(docker image inspect --format $format $Image 2>$null)
        if ($LASTEXITCODE -ne 0 -or $inspect.Count -ne 1) { return }
        $fields = "$($inspect[0])".Trim().Split(' ', 2)
        if ($effectiveManifest) {
            if ($fields.Count -ne 2) { return }
            try {
                $labels = ConvertFrom-Json -InputObject $fields[1] -ErrorAction Stop
            } catch {
                return
            }
            if ($labels.$imageLabel -cne $fingerprint) { return }
        }
        $fields[0]
    }

    $imageId = if (-not $Rebuild) { Resolve-ToolsImage }
    if (-not $imageId) {
        $context = Join-Path $out 'empty-build-context'
        New-Item -ItemType Directory -Force -Path $context | Out-Null
        Write-Log "Building $Image from snapshot infra/Dockerfile.tools sha256=$fingerprint (empty build context) ..."
        docker build -t $Image --label "$imageLabel=$fingerprint" -f $dockerfile $context
        if ($LASTEXITCODE -ne 0) { Stop-Run 'tools image build failed' 1 }
        $imageId = Resolve-ToolsImage
    }
    if (-not $imageId) { Stop-Run "cannot resolve verified image ID for $Image (Dockerfile sha256=$fingerprint)" 1 }

    Write-Log "verify-linux mode=$Mode filter='$effectiveFilter' manifest_sha256=$($script:manifestField) worktree=$root branch=$branch head=$head uncommitted+untracked=$dirty"
    Write-Log "image=$Image id=$imageId dockerfile-sha256=$fingerprint snapshot-files=$copied source=$snapshotSource (git-ignored files excluded) docker-socket=$(if ($requiresPgLock) { 'mounted' } else { 'not mounted' }) pg-lock=$(if ($requiresPgLock) { $script:lockScopeHash } else { 'not required' })"

    # 4. Run.
    $dockerArgs = @('run', '--rm')
    if ($requiresPgLock) {
        $dockerArgs += @(
            '--name', $script:containerName,
            '--label', 'org.tra-hien.verify-role=heavy-pg',
            '--label', "org.tra-hien.verify-lock=$($script:lockScopeHash)",
            '--label', "org.tra-hien.verify-run-id=$($script:runId)",
            '--add-host=host.docker.internal:host-gateway',
            '-v', '//var/run/docker.sock:/var/run/docker.sock',
            '-e', 'TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal'
        )
    }
    $dockerArgs += @(
        '-v', "$($snapshot -replace '\\', '/'):/src:ro",
        '-v', "$($out -replace '\\', '/'):/out"
    )
    if ($Mode -ne 'docs') {
        $dockerArgs += @('-v', 'tra-hien-tools-cache:/cache')
    }
    $dockerArgs += @(
        '-e', "TEST_FILTER=$effectiveFilter",
        $imageId,
        # Strip CR defensively in case the script was checked out with CRLF.
        'bash', '-c', 'tr -d \\r < /src/scripts/verify-linux.sh > /tmp/verify.sh && exec bash /tmp/verify.sh $0', $Mode
    )
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & docker @dockerArgs | Tee-Object -FilePath $log -Append
    $rc = $LASTEXITCODE
    $elapsed = [int]$watch.Elapsed.TotalSeconds

    Release-HeavyPgLock
    Cleanup-Snapshot

    # 5. API outputs: both must exist; with -WriteBack every copy must succeed and match by hash.
    if ($Mode -eq 'api' -and $rc -eq 0) {
        $pairs = @(
            @((Join-Path $out 'api-client/openapi.json'), (Join-Path $root 'packages/api-client/openapi.json')),
            @((Join-Path $out 'api-client/schema.ts'), (Join-Path $root 'packages/api-client/src/generated/schema.ts'))
        )
        $missing = @($pairs | Where-Object { -not (Test-Path -LiteralPath $_[0] -PathType Leaf) })
        if ($missing.Count -gt 0) { Stop-Run "generated output missing: $(($missing | ForEach-Object { $_[0] }) -join ', ')" 3 }
        foreach ($pair in $pairs) {
            $target = $pair[1].Substring($root.Length + 1)
            try {
                $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pair[0] -ErrorAction Stop).Hash.ToLowerInvariant()
                if ($WriteBack) {
                    Copy-Item -LiteralPath $pair[0] -Destination $pair[1] -Force -ErrorAction Stop
                    $written = (Get-FileHash -Algorithm SHA256 -LiteralPath $pair[1] -ErrorAction Stop).Hash.ToLowerInvariant()
                    if ($written -ne $hash) { Stop-Run "hash mismatch after writing $target" 3 }
                    Write-Log "generated $target sha256=$hash (written to worktree, hash verified)"
                } else {
                    Write-Log "generated $target sha256=$hash (not written; use -WriteBack)"
                }
            } catch {
                Stop-Run "cannot hash or write $target`: $($_.Exception.Message)" 3
            }
        }
    }

    if ($rc -eq 0) {
        $resultToken = if ($hasFilter) { 'FOCUSED_PASS' } else { 'PASS' }
        Write-Log "verify-linux RESULT=$resultToken mode=$Mode coverage=$plannedCoverage manifest_sha256=$($script:manifestField) exit_code=0 elapsed=${elapsed}s log=$log"
    } else {
        Write-Log "verify-linux RESULT=FAIL mode=$Mode coverage=NONE manifest_sha256=$($script:manifestField) exit_code=$rc elapsed=${elapsed}s log=$log"
    }
    exit $rc
} catch {
    Stop-Run "unexpected verification failure: $($_.Exception.Message)" 1
} finally {
    Release-HeavyPgLock
    Cleanup-Snapshot
}
