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
    backend/full modes; api/frontend run without it. Mounting the socket gives the container control of
    the local Docker engine, so run this only on the developer machine with trusted repository code.
   - The image is built from infra/Dockerfile.tools in the snapshot and runs by immutable image ID.
     Manifest runs require the image's recipe hash label to match the verified Dockerfile.

  Logs, TRX and migrations.sql go to artifacts/verify-linux/<timestamp>-<mode>/ (git-ignored). Sandboxed
  agents must run this script with sandbox bypass/approval because it talks to the Docker engine.
.EXAMPLE
  ./scripts/verify-linux.ps1
  Same as the CI backend job: build, format, all backend tests, zero-skip check, migrations, check:api.
.EXAMPLE
  ./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Inventory"
  Focused tests during G3. A full run is still required before READY_FOR_LOCAL_G4.
.EXAMPLE
  ./scripts/verify-linux.ps1 -Mode api -WriteBack
  Regenerate packages/api-client/openapi.json and src/generated/schema.ts into this worktree.
.EXAMPLE
  ./scripts/verify-linux.ps1 -Mode full
  Backend job plus frontend job (lint, typecheck, tests, builds).
.EXAMPLE
  ./scripts/verify-linux.ps1 -Manifest artifacts/p1-10/candidate.manifest
  Final G3 run bound to the candidate: fails if the worktree differs from the manifest, then builds the
  snapshot from git objects whose IDs equal the manifest (base tree + reviewed entries), so the container
  runs exactly the fingerprinted candidate; the log records the manifest SHA-256.
#>
[CmdletBinding()]
param(
    [ValidateSet('backend', 'api', 'frontend', 'full')][string]$Mode = 'backend',
    [string]$Filter = '',
    [switch]$WriteBack,
    [switch]$Rebuild,
    [string]$Image = '',
    [string]$Manifest = ''
)

# Native tools report failures through exit codes; stderr must not abort Windows PowerShell 5.1.
$ErrorActionPreference = 'Continue'
$Mode = $Mode.ToLowerInvariant()   # ValidateSet is case-insensitive; the Linux script is not.
$root = Split-Path -Parent $PSScriptRoot
if ($WriteBack -and $Mode -ne 'api') { Write-Error '-WriteBack is only valid with -Mode api.'; exit 2 }
if ($Manifest -and $Image) { Write-Error '-Image cannot be combined with -Manifest. Use the candidate tools image.'; exit 2 }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$out = Join-Path $root "artifacts/verify-linux/$stamp-$Mode"
$snapshot = Join-Path $root "artifacts/verify-linux/.snapshots/$stamp-$Mode"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'verify.log'
function Write-Log([string]$Message) { $Message | Tee-Object -FilePath $log -Append }
function Stop-Run([string]$Message, [int]$Code) {
    Write-Log "verify-linux RESULT=FAIL (exit $Code) mode=$Mode reason=$Message log=$log"
    if (Test-Path -LiteralPath $snapshot) { Remove-Item -LiteralPath $snapshot -Recurse -Force -ErrorAction SilentlyContinue }
    exit $Code
}

# 1. Filtered snapshot: tracked + untracked-not-ignored files only.
$previousOutput = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$paths = @(git -c core.quotepath=false -C $root ls-files -co --exclude-standard --deduplicate)
$listRc = $LASTEXITCODE
[Console]::OutputEncoding = $previousOutput
if ($listRc -ne 0 -or $paths.Count -eq 0) { Stop-Run 'git ls-files failed' 1 }
# Secret-like names (the .gitignore secret patterns) are refused on the host BEFORE anything is copied,
# including files force-added to the index. infra/.env.example and *.env.*.example remain allowed.
function Test-SecretPath([string]$Path) {
    $normalized = $Path -replace '\\', '/'
    $leaf = ($normalized -split '/')[-1]
    if ($leaf -match '^\.env(\..+)?$' -and $leaf -notmatch '\.example$') { return $true }
    if ($leaf -match '\.(pfx|p12|pem|key|secret)$') { return $true }
    return ($normalized -match '(^|/)secrets/')
}
$secretPaths = @($paths | Where-Object { (Test-SecretPath $_) -and (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) })
if ($secretPaths.Count -gt 0) { Stop-Run "secret-like files would enter the container (remove them from the index/worktree): $($secretPaths -join ', ')" 4 }
$copied = 0
$snapshotSource = 'worktree copy (tracked + untracked-not-ignored)'
if ($Manifest) {
    # Read the manifest file ONCE into memory and compute its SHA-256 from those exact bytes so a
    # concurrent edit to the manifest file cannot cause a mismatch between the logged digest and the
    # materialized snapshot. Build the snapshot from immutable git objects (read-tree + cacheinfo +
    # checkout-index), then verify the materialized snapshot directory on disk before logging MATCH.
    $manifestFile = if ([IO.Path]::IsPathRooted($Manifest)) { $Manifest } else { Join-Path $root $Manifest }
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { Stop-Run "manifest not found: $manifestFile" 1 }
    $manifestBytes = [IO.File]::ReadAllBytes($manifestFile)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $manifestDigest = ([BitConverter]::ToString($sha256.ComputeHash($manifestBytes)) -replace '-', '').ToLowerInvariant() }
    finally { $sha256.Dispose() }
    $manifestHeader = '# candidate-manifest v2 base='
    $lines = @(([Text.Encoding]::UTF8.GetString($manifestBytes) -split "\r?\n") | Where-Object { $_ -ne '' })
    if ($lines.Count -lt 2 -or -not $lines[0].StartsWith($manifestHeader) -or
        $lines[0].Substring($manifestHeader.Length) -notmatch '^[0-9a-f]{40,64}$') {
        Stop-Run 'invalid or outdated manifest header (expected candidate-manifest v2)' 1
    }
    $baseSha = $lines[0].Substring($manifestHeader.Length)
    $entries = @()
    $expectedMap = @{}
    foreach ($line in $lines[1..($lines.Count - 1)]) {
        $parts = $line.Split(' ', 3)
        if ($parts.Count -ne 3 -or -not $parts[2] -or $parts[2].Contains('\') -or
            [IO.Path]::IsPathRooted($parts[2]) -or ($parts[2] -split '/') -contains '..' -or
            $expectedMap.ContainsKey($parts[2])) {
            Stop-Run "invalid manifest entry: $line" 1
        }
        if ($parts[1] -ne 'DELETED' -and (Test-SecretPath $parts[2])) {
            Stop-Run "secret-like file listed in manifest: $($parts[2])" 4
        }
        $expectedMap[$parts[2]] = "$($parts[0]) $($parts[1])"
        $entries += [pscustomobject]@{ Mode = $parts[0]; Blob = $parts[1]; Path = $parts[2] }
    }

    # Verify the working tree against the in-memory manifest while writing candidate blobs into .git/objects.
    $previousEnc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        $wtTracked = @(git -c core.quotepath=false -C $root diff --name-only --no-renames $baseSha)
        if ($LASTEXITCODE -ne 0) { Stop-Run "git diff $baseSha failed" 1 }
        $wtUntracked = @(git -c core.quotepath=false -C $root ls-files -o --exclude-standard)
        if ($LASTEXITCODE -ne 0) { Stop-Run 'git ls-files -o failed' 1 }
        $indexModes = @{}
        foreach ($idxLine in @(git -c core.quotepath=false -C $root ls-files -s)) {
            $tab = $idxLine.IndexOf("`t")
            if ($tab -gt 0) { $indexModes[$idxLine.Substring($tab + 1)] = ($idxLine.Substring(0, $tab) -split ' ')[0] }
        }
    } finally {
        [Console]::OutputEncoding = $previousEnc
    }
    $wtPaths = @($wtTracked + $wtUntracked | Where-Object { $_ } | Sort-Object -Unique)
    $wtPresent = @($wtPaths | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    $actualMap = @{}
    if ($wtPresent.Count -gt 0) {
        $ids = @($wtPresent | & git -C $root hash-object -w --stdin-paths)
        if ($LASTEXITCODE -ne 0 -or $ids.Count -ne $wtPresent.Count) { Stop-Run 'git hash-object -w failed' 1 }
        for ($i = 0; $i -lt $wtPresent.Count; $i++) {
            $p = $wtPresent[$i]
            $m = if ($indexModes.ContainsKey($p)) { $indexModes[$p] } else { '100644' }
            $actualMap[$p] = "$m $($ids[$i].Trim())"
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
        if ($deleted.Count -gt 0) {
            & git -C $root update-index --force-remove @deleted
            if ($LASTEXITCODE -ne 0) { Stop-Run 'git update-index --force-remove failed' 1 }
        }
        $stageLines = @(git -c core.quotepath=false -C $root ls-files -s)
        if ($LASTEXITCODE -ne 0 -or $stageLines.Count -eq 0) { Stop-Run 'git ls-files -s on temporary index failed' 1 }
        $snapshotPaths = @()
        $expectedSnapshotBlobs = @()
        foreach ($sLine in $stageLines) {
            $tab = $sLine.IndexOf("`t")
            $sPath = $sLine.Substring($tab + 1)
            if (Test-SecretPath $sPath) { Stop-Run "secret-like files in the candidate: $sPath" 4 }
            $snapshotPaths += $sPath
            $expectedSnapshotBlobs += ($sLine.Substring(0, $tab) -split ' ')[1]
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
    $materializedBlobs = @(($snapshotPaths | ForEach-Object { Join-Path $snapshot $_ }) | & git -C $root hash-object --no-filters --stdin-paths)
    if ($LASTEXITCODE -ne 0 -or $materializedBlobs.Count -ne $expectedSnapshotBlobs.Count) {
        Stop-Run 'post-materialization snapshot hash failed' 1
    }
    for ($i = 0; $i -lt $expectedSnapshotBlobs.Count; $i++) {
        if ("$($materializedBlobs[$i])".Trim() -ne $expectedSnapshotBlobs[$i]) {
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
} else {
    foreach ($relative in $paths) {
        $source = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }   # tracked but deleted in the worktree
        $target = Join-Path $snapshot $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        [IO.File]::Copy($source, $target, $true)
        $copied++
    }
}

# 2. Resolve the tools image from the snapshot recipe, including on cache hits.
$dockerfile = Join-Path $snapshot 'infra/Dockerfile.tools'
try {
    $fingerprint = (Get-FileHash -Algorithm SHA256 -LiteralPath $dockerfile -ErrorAction Stop).Hash.ToLowerInvariant()
} catch {
    Stop-Run 'cannot hash infra/Dockerfile.tools in the snapshot' 1
}
$imageLabel = 'org.tra-hien.tools.dockerfile-sha256'
if (-not $Image) { $Image = "tra-hien-tools:$($fingerprint.Substring(0, 12))" }

function Resolve-ToolsImage {
    $format = if ($Manifest) { '{{.Id}} {{json .Config.Labels}}' } else { '{{.Id}}' }
    $inspect = @(docker image inspect --format $format $Image 2>$null)
    if ($LASTEXITCODE -ne 0 -or $inspect.Count -ne 1) { return }
    $fields = "$($inspect[0])".Trim().Split(' ', 2)
    if ($Manifest) {
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

$head = git -C $root rev-parse HEAD
$branch = git -C $root rev-parse --abbrev-ref HEAD
$dirty = @(git -C $root status --porcelain).Count
Write-Log "verify-linux mode=$Mode filter='$Filter' worktree=$root branch=$branch head=$head uncommitted+untracked=$dirty"
Write-Log "image=$Image id=$imageId dockerfile-sha256=$fingerprint snapshot-files=$copied source=$snapshotSource (git-ignored files excluded) docker-socket=$(if ($Mode -in 'backend', 'full') { 'mounted' } else { 'not mounted' })"

# 3. Run.
$dockerArgs = @('run', '--rm')
if ($Mode -in 'backend', 'full') {
    $dockerArgs += @('--add-host=host.docker.internal:host-gateway',
        '-v', '//var/run/docker.sock:/var/run/docker.sock',
        '-e', 'TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal')
}
$dockerArgs += @(
    '-v', "$($snapshot -replace '\\', '/'):/src:ro",
    '-v', "$($out -replace '\\', '/'):/out",
    '-v', 'tra-hien-tools-cache:/cache',
    '-e', "TEST_FILTER=$Filter",
    $imageId,
    # Strip CR defensively in case the script was checked out with CRLF.
    'bash', '-c', 'tr -d \\r < /src/scripts/verify-linux.sh > /tmp/verify.sh && exec bash /tmp/verify.sh $0', $Mode
)
$watch = [Diagnostics.Stopwatch]::StartNew()
& docker @dockerArgs | Tee-Object -FilePath $log -Append
$rc = $LASTEXITCODE
$elapsed = [int]$watch.Elapsed.TotalSeconds
Remove-Item -LiteralPath $snapshot -Recurse -Force -ErrorAction SilentlyContinue

# 4. API outputs: both must exist; with -WriteBack every copy must succeed and match by hash.
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

$status = if ($rc -eq 0) { 'PASS' } else { "FAIL (exit $rc)" }
Write-Log "verify-linux RESULT=$status mode=$Mode elapsed=${elapsed}s log=$log"
exit $rc
