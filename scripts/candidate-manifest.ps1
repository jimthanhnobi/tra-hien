<#
.SYNOPSIS
  Content fingerprint of a task candidate for Tier A and Tier B: git mode + blob ID of every changed,
  deleted and untracked (not ignored) path relative to a base commit.
.DESCRIPTION
  G3 writes the manifest; G4 verifies the working tree against it before reviewing; publish-pr verifies
  the staged index against it immediately before committing. Any content or mode change after review
  (for example `git update-index --chmod=+x`) changes an entry, so unreviewed bytes or permissions cannot
  reach the commit unnoticed. Blob IDs use git's own clean/eol conversion (git hash-object), the same
  bytes git add stores. Working-tree modes come from the index entry when one exists (core.filemode is
  false on Windows), otherwise 100644 for a new file. Secret-like paths (.env*, *.pem, *.key, *.pfx,
  *.p12, *.secret, secrets/) are refused when writing; *.example files are allowed.
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Write -Base origin/main -Out artifacts/p1-10/candidate.manifest
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest
  Working tree (staged + unstaged + untracked) must equal the reviewed candidate.
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest -Index
  Staged index (mode + blob) must equal the reviewed candidate (run right before git commit).
#>
[CmdletBinding(DefaultParameterSetName = 'verify')]
param(
    [Parameter(ParameterSetName = 'write', Mandatory)][switch]$Write,
    [Parameter(ParameterSetName = 'write')][string]$Base = 'origin/main',
    [Parameter(ParameterSetName = 'write', Mandatory)][string]$Out,
    [Parameter(ParameterSetName = 'verify', Mandatory)][string]$Verify,
    [Parameter(ParameterSetName = 'verify')][switch]$Index
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$header = '# candidate-manifest v2 base='

function Invoke-GitLines {
    # Emits one string per line; callers wrap the call in @() to get an array.
    $lines = @(& git -c core.quotepath=false -C $root @args)
    if ($LASTEXITCODE -ne 0) { Write-Error "git $($args -join ' ') failed."; exit 1 }
    $lines
}

function Test-SecretPath([string]$Path) {
    $leaf = ($Path -split '/')[-1]
    if ($leaf -match '^\.env(\..+)?$' -and $leaf -notmatch '\.example$') { return $true }
    if ($leaf -match '\.(pfx|p12|pem|key|secret)$') { return $true }
    return ($Path -match '(^|/)secrets/')
}

function Get-IndexTable {
    # path -> @{ Mode; Blob } for stage-0 entries of the current index.
    $table = @{}
    foreach ($line in @(Invoke-GitLines ls-files -s)) {
        $tab = $line.IndexOf("`t")
        $meta = $line.Substring(0, $tab) -split ' '
        $table[$line.Substring($tab + 1)] = @{ Mode = $meta[0]; Blob = $meta[1] }
    }
    return $table
}

function Get-WorktreeEntries([string]$BaseSha) {
    $tracked = @(Invoke-GitLines diff --name-only --no-renames $BaseSha)
    $untracked = @(Invoke-GitLines ls-files -o --exclude-standard)
    $paths = @($tracked + $untracked | Where-Object { $_ } | Sort-Object -Unique)
    $indexTable = Get-IndexTable
    $entries = [ordered]@{}
    $present = @($paths | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    if ($present.Count -gt 0) {
        $ids = @($present | & git -C $root hash-object --stdin-paths)
        if ($LASTEXITCODE -ne 0 -or $ids.Count -ne $present.Count) { Write-Error 'git hash-object failed.'; exit 1 }
        for ($i = 0; $i -lt $present.Count; $i++) {
            $mode = if ($indexTable.ContainsKey($present[$i])) { $indexTable[$present[$i]].Mode } else { '100644' }
            $entries[$present[$i]] = "$mode $($ids[$i])"
        }
    }
    foreach ($path in $paths) { if (-not $entries.Contains($path)) { $entries[$path] = '- DELETED' } }
    return $entries
}

function Get-IndexEntries([string]$BaseSha) {
    $paths = @(@(Invoke-GitLines diff --cached --name-only --no-renames $BaseSha) | Where-Object { $_ } | Sort-Object -Unique)
    $indexTable = Get-IndexTable
    $entries = [ordered]@{}
    foreach ($path in $paths) {
        $entries[$path] = if ($indexTable.ContainsKey($path)) { "$($indexTable[$path].Mode) $($indexTable[$path].Blob)" } else { '- DELETED' }
    }
    return $entries
}

function Format-Manifest([string]$BaseSha, $Entries) {
    $keys = [string[]]@($Entries.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $lines = @("$header$BaseSha")
    foreach ($path in $keys) { $lines += "$($Entries[$path]) $path" }
    return $lines
}

if ($Write) {
    $resolved = @(& git -C $root rev-parse --verify "$Base^{commit}")
    $resolveRc = $LASTEXITCODE
    $baseSha = if ($resolved.Count -gt 0) { "$($resolved[0])".Trim() } else { '' }
    if ($resolveRc -ne 0 -or -not $baseSha) { Write-Error "Base '$Base' does not resolve to a commit."; exit 1 }
    $entries = Get-WorktreeEntries $baseSha
    if ($entries.Count -eq 0) { Write-Error 'No changes relative to the base; nothing to fingerprint.'; exit 1 }
    $secrets = @($entries.Keys | Where-Object { (Test-SecretPath $_) -and $entries[$_] -ne '- DELETED' })
    if ($secrets.Count -gt 0) { Write-Error "Secret-like files must not be part of a candidate: $($secrets -join ', ')"; exit 1 }
    $target = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [IO.File]::WriteAllLines($target, [string[]](Format-Manifest $baseSha $entries), $utf8)
    $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant()
    Write-Output "MANIFEST: $target"
    Write-Output "BASE:     $baseSha"
    Write-Output "ENTRIES:  $($entries.Count)"
    Write-Output "SHA256:   $digest"
    exit 0
}

$manifestPath = if ([IO.Path]::IsPathRooted($Verify)) { $Verify } else { Join-Path $root $Verify }
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { Write-Error "Manifest not found: $manifestPath"; exit 1 }
$expectedLines = @([IO.File]::ReadAllLines($manifestPath, $utf8))
if ($expectedLines.Count -lt 2 -or -not $expectedLines[0].StartsWith($header) -or
    $expectedLines[0].Substring($header.Length) -notmatch '^[0-9a-f]{40,64}$') {
    Write-Error 'Invalid or outdated manifest header (expected candidate-manifest v2); regenerate it with -Write.'; exit 1
}
$baseSha = $expectedLines[0].Substring($header.Length)
$actual = if ($Index) { Get-IndexEntries $baseSha } else { Get-WorktreeEntries $baseSha }
$expected = @{}
foreach ($line in $expectedLines[1..($expectedLines.Count - 1)]) {
    $parts = $line.Split(' ', 3)   # mode, blob-or-DELETED, path
    $expected[$parts[2]] = "$($parts[0]) $($parts[1])"
}
$problems = @()
foreach ($path in $expected.Keys) {
    if (-not $actual.Contains($path)) { $problems += "MISSING  $path (reviewed $($expected[$path]))" }
    elseif ($actual[$path] -ne $expected[$path]) { $problems += "CHANGED  $path (reviewed $($expected[$path]), now $($actual[$path]))" }
}
foreach ($path in $actual.Keys) { if (-not $expected.ContainsKey($path)) { $problems += "EXTRA    $path ($($actual[$path]))" } }
$digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
$scope = if ($Index) { 'staged index' } else { 'working tree' }
if ($problems.Count -gt 0) {
    Write-Output "CANDIDATE MISMATCH ($scope vs manifest sha256 $digest):"
    $problems | Sort-Object | ForEach-Object { Write-Output "  $_" }
    exit 1
}
Write-Output "CANDIDATE MATCH: $scope == manifest sha256 $digest ($($actual.Count) entries, base $baseSha)"
exit 0
