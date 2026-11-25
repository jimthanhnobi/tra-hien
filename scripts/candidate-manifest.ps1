<#
.SYNOPSIS
  Content fingerprint of a task candidate for Tier A and Tier B: git mode + blob ID of every changed,
  deleted and untracked (not ignored) path relative to a base commit.
.DESCRIPTION
  G3 writes the manifest; G4 verifies the working tree against it before reviewing; publish-pr verifies
  working tree and staged index against it immediately before committing. Any content or mode change after
  review (for example `git update-index --chmod=+x`) changes an entry, so unreviewed bytes or permissions
  cannot reach the commit unnoticed. Blob IDs use git's own clean/eol conversion (git hash-object), the same
  bytes git add stores. Working-tree modes come from the index entry when one exists (core.filemode is
  false on Windows), otherwise 100644 for a new file. Secret-like paths (.env*, *.pem, *.key, *.pfx,
  *.p12, *.secret, secrets/) are refused when writing or verifying; *.example files are allowed.

  Candidate integrity and base freshness are intentionally separate: default `-Verify` checks candidate
  content against the manifest's recorded base. Pass `-RequireBase <ref|sha>` to enforce base freshness,
  `-Both` to verify working tree and staged index together, and `-PublishMode initial|republish` (with
  optional `-PublishedHead <ref|sha>` in republish mode) to enforce local publish Git topology.
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Write -Base origin/main -Out artifacts/p1-10/candidate.manifest
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest
  Working tree (staged + unstaged + untracked) must equal the reviewed candidate.
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest -Index
  Staged index (mode + blob) must equal the reviewed candidate (run right before git commit).
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest -Both -RequireBase origin/main -PublishMode initial
  Verify working tree, staged index, base freshness, and initial publish topology together.
.EXAMPLE
  ./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest -Both -RequireBase origin/main -PublishMode republish -PublishedHead origin/feature/my-task
  Verify a published branch before CI remediation or post-base-integration merge commit.
#>
[CmdletBinding(DefaultParameterSetName = 'verify')]
param(
    [Parameter(ParameterSetName = 'write', Mandatory)][switch]$Write,
    [Parameter(ParameterSetName = 'write')][string]$Base = 'origin/main',
    [Parameter(ParameterSetName = 'write', Mandatory)][string]$Out,
    [Parameter(ParameterSetName = 'verify', Mandatory)][string]$Verify,
    [Parameter(ParameterSetName = 'verify')][switch]$Index,
    [Parameter(ParameterSetName = 'verify')][switch]$Both,
    [Parameter(ParameterSetName = 'verify')][string]$RequireBase = '',
    [Parameter(ParameterSetName = 'verify')][ValidateSet('initial', 'republish')][string]$PublishMode,
    [Parameter(ParameterSetName = 'verify')][string]$PublishedHead = ''
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$canonicalRoot = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
$utf8 = [Text.UTF8Encoding]::new($false)
$header = '# candidate-manifest v2 base='

if ($PSCmdlet.ParameterSetName -eq 'verify') {
    if ($Index -and $Both) {
        Write-Error '-Index and -Both are mutually exclusive.'
        exit 2
    }
    if ($PSBoundParameters.ContainsKey('RequireBase') -and [string]::IsNullOrWhiteSpace($RequireBase)) {
        Write-Error '-RequireBase cannot be empty or whitespace-only.'
        exit 2
    }
    if ($PSBoundParameters.ContainsKey('PublishedHead')) {
        if ([string]::IsNullOrWhiteSpace($PublishedHead)) {
            Write-Error '-PublishedHead cannot be empty or whitespace-only.'
            exit 2
        }
        if ($PublishMode -ne 'republish') {
            Write-Error '-PublishedHead is only valid with -PublishMode republish.'
            exit 2
        }
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
        if ($proc.ExitCode -ne 0) {
            Write-Error "git $($GitArgs -join ' ') failed: $stderr"
            exit 1
        }
        return $ms.ToArray()
    } finally {
        $ms.Dispose()
        $proc.Dispose()
    }
}

function Parse-GitZPaths([byte[]]$Bytes) {
    if ($Bytes.Length -eq 0) { return @() }
    if ($Bytes[$Bytes.Length - 1] -ne 0) { Write-Error 'Incomplete NUL-delimited git path output.'; exit 1 }
    $text = $utf8.GetString($Bytes, 0, $Bytes.Length - 1)
    if ($text.Length -eq 0) { return @() }
    return @($text.Split([char]0) | Where-Object { $_ -ne '' })
}

function Invoke-GitHashObjects([string[]]$Paths) {
    $allHashes = @()
    if ($Paths.Count -eq 0) { return $allHashes }
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
            $gitArgs = @('hash-object', '--') + $batch.ToArray()
            $bytes = Invoke-GitBytes $gitArgs
            $stdout = $utf8.GetString($bytes)
            $allHashes += @($stdout -split "\r?\n" | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim() })
            $batch.Clear()
            $batchChars = 0
        }
        if (-not $atEnd) {
            [void]$batch.Add($Paths[$i])
            $batchChars += $nextLen
        }
    }
    return $allHashes
}

function Resolve-CommitSha([string]$Ref) {
    $resolved = @(& git -C $root rev-parse --verify --quiet "$Ref^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $resolved.Count -eq 0) { return '' }
    $sha = "$($resolved[0])".Trim()
    if ($sha -cnotmatch '^[0-9a-f]{40,64}$') { return '' }
    return $sha
}

function Get-GitMetaPath([string]$Name) {
    $p = "$(& git -C $root rev-parse --git-path $Name 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $p) { return '' }
    if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path $root $p }
    return $p
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

function Assert-SafeRepoPathsOnDisk([string[]]$RelPaths) {
    $safeDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $rootPrefix = $canonicalRoot + [IO.Path]::DirectorySeparatorChar
    foreach ($rel in $RelPaths) {
        if (-not (Test-SafeRepoRelPath $rel)) {
            Write-Output "UNSAFE PATH: invalid repository relative path '$rel'"
            exit 1
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
                Write-Output "UNSAFE PATH: cannot inspect attributes of '$current' for '$rel': $($_.Exception.Message)"
                exit 1
            }
            if (($attrs -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Output "UNSAFE PATH: symlink, junction, or reparse point at '$current' for '$rel' is not allowed"
                exit 1
            }
            if (-not $isLeaf) { [void]$safeDirs.Add($current) }
        }
        $fullTarget = [IO.Path]::GetFullPath((Join-Path $canonicalRoot $rel))
        if (-not $fullTarget.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Output "UNSAFE PATH: '$rel' escapes repository root '$canonicalRoot'"
            exit 1
        }
    }
}

function Assert-NoUnmergedIndex {
    $unmergedBytes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-u', '-z')
    $unmerged = @(Parse-GitZPaths $unmergedBytes)
    if ($unmerged.Count -gt 0) {
        $paths = @($unmerged | ForEach-Object {
            $tab = $_.IndexOf("`t")
            if ($tab -ge 0) { $_.Substring($tab + 1) } else { $_ }
        } | Sort-Object -Unique)
        Write-Output "CANDIDATE MISMATCH (unresolved merge conflicts in index): $($paths -join ', ')"
        exit 1
    }
}

function Get-IndexTable {
    # path -> @{ Mode; Blob } for stage-0 entries of the current index.
    $table = @{}
    $bytes = Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-s', '-z')
    foreach ($line in @(Parse-GitZPaths $bytes)) {
        $tab = $line.IndexOf("`t")
        if ($tab -le 0) { continue }
        $meta = $line.Substring(0, $tab) -split ' '
        if ($meta.Count -lt 3) { continue }
        if ($meta[2] -ne '0') {
            Write-Output "CANDIDATE MISMATCH (unresolved merge stage $($meta[2]) in index): $($line.Substring($tab + 1))"
            exit 1
        }
        $table[$line.Substring($tab + 1)] = @{ Mode = $meta[0]; Blob = $meta[1] }
    }
    return $table
}

function Get-WorktreeEntries([string]$BaseSha) {
    $tracked = @(Parse-GitZPaths (Invoke-GitBytes @('-c', 'core.quotepath=false', 'diff', '--name-only', '--no-renames', '-z', $BaseSha)))
    $untracked = @(Parse-GitZPaths (Invoke-GitBytes @('-c', 'core.quotepath=false', 'ls-files', '-o', '--exclude-standard', '-z')))
    $paths = @($tracked + $untracked | Where-Object { $_ } | Sort-Object -Unique)
    if ($paths.Count -gt 0) { Assert-SafeRepoPathsOnDisk $paths }
    $indexTable = Get-IndexTable
    $entries = [ordered]@{}
    $present = @($paths | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    if ($present.Count -gt 0) {
        $ids = @(Invoke-GitHashObjects -Paths $present)
        if ($ids.Count -ne $present.Count) { Write-Error 'git hash-object returned unexpected count.'; exit 1 }
        for ($i = 0; $i -lt $present.Count; $i++) {
            $mode = if ($indexTable.ContainsKey($present[$i])) { $indexTable[$present[$i]].Mode } else { '100644' }
            $entries[$present[$i]] = "$mode $($ids[$i])"
        }
    }
    foreach ($path in $paths) { if (-not $entries.Contains($path)) { $entries[$path] = '- DELETED' } }
    return $entries
}

function Get-IndexEntries([string]$BaseSha) {
    $paths = @(@(Parse-GitZPaths (Invoke-GitBytes @('-c', 'core.quotepath=false', 'diff', '--cached', '--name-only', '--no-renames', '-z', $BaseSha))) | Where-Object { $_ } | Sort-Object -Unique)
    $indexTable = Get-IndexTable
    $entries = [ordered]@{}
    foreach ($path in $paths) {
        $entries[$path] = if ($indexTable.ContainsKey($path)) { "$($indexTable[$path].Mode) $($indexTable[$path].Blob)" } else { '- DELETED' }
    }
    return $entries
}

function Compare-ManifestEntries($Expected, $Actual) {
    $problems = @()
    foreach ($path in $Expected.Keys) {
        if (-not $Actual.Contains($path)) { $problems += "MISSING  $path (reviewed $($Expected[$path]))" }
        elseif ($Actual[$path] -ne $Expected[$path]) { $problems += "CHANGED  $path (reviewed $($Expected[$path]), now $($Actual[$path]))" }
    }
    foreach ($path in $Actual.Keys) {
        if (-not $Expected.ContainsKey($path)) { $problems += "EXTRA    $path ($($Actual[$path]))" }
    }
    return @($problems | Sort-Object)
}

function Format-Manifest([string]$BaseSha, $Entries) {
    $keys = [string[]]@($Entries.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $lines = @("$header$BaseSha")
    foreach ($path in $keys) { $lines += "$($Entries[$path]) $path" }
    return $lines
}

function Assert-PublishTopology([string]$Mode, [string]$BaseSha, [string]$ExpectedPublishedHead) {
    foreach ($op in 'rebase-merge', 'rebase-apply', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'BISECT_LOG') {
        $opPath = Get-GitMetaPath $op
        if ($opPath -and (Test-Path -LiteralPath $opPath)) {
            Write-Output "TOPOLOGY MISMATCH ($Mode): Git operation '$op' is in progress."
            exit 1
        }
    }

    $branch = "$(& git -C $root symbolic-ref --quiet --short HEAD 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $branch) {
        Write-Output "TOPOLOGY MISMATCH ($Mode): detached HEAD is not allowed for publish."
        exit 1
    }

    $headSha = Resolve-CommitSha 'HEAD'
    if (-not $headSha) {
        Write-Output "TOPOLOGY MISMATCH ($Mode): cannot resolve HEAD to a commit."
        exit 1
    }

    $mergeHeadPath = Get-GitMetaPath 'MERGE_HEAD'
    $mergeHeads = @()
    if ($mergeHeadPath -and (Test-Path -LiteralPath $mergeHeadPath -PathType Leaf)) {
        $mergeHeads = @([IO.File]::ReadAllLines($mergeHeadPath, $utf8) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($mergeHeads.Count -eq 0) {
            Write-Output "TOPOLOGY MISMATCH ($Mode): MERGE_HEAD file exists but is empty."
            exit 1
        }
    }

    if ($Mode -eq 'initial') {
        if ($mergeHeads.Count -gt 0) {
            Write-Output "TOPOLOGY MISMATCH (initial): unexpected MERGE_HEAD present ($($mergeHeads -join ', ')); use task-integrate for unpublished base integration."
            exit 1
        }
        if ($headSha -cne $BaseSha) {
            Write-Output "TOPOLOGY MISMATCH (initial): HEAD ($headSha) != manifest base ($BaseSha). Unpublished branch must sit directly on manifest base."
            exit 1
        }
        return
    }

    # republish mode
    if ($mergeHeads.Count -gt 0) {
        if ($mergeHeads.Count -ne 1) {
            Write-Output "TOPOLOGY MISMATCH (republish): unexpected multi-head MERGE_HEAD ($($mergeHeads.Count) entries)."
            exit 1
        }
        $mergeHeadSha = $mergeHeads[0]
        if ($mergeHeadSha -cnotmatch '^[0-9a-f]{40,64}$' -or $mergeHeadSha -cne $BaseSha) {
            Write-Output "TOPOLOGY MISMATCH (republish): MERGE_HEAD ($mergeHeadSha) != manifest base ($BaseSha)."
            exit 1
        }
        $commonBase = @(& git -C $root merge-base $headSha $BaseSha 2>$null)
        if ($LASTEXITCODE -ne 0 -or $commonBase.Count -eq 0) {
            Write-Output "TOPOLOGY MISMATCH (republish): HEAD ($headSha) and manifest base ($BaseSha) share no common ancestor."
            exit 1
        }
        & git -C $root merge-base --is-ancestor $headSha $BaseSha 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Output "TOPOLOGY MISMATCH (republish): HEAD ($headSha) is already an ancestor of base ($BaseSha); branch has no published commits to merge."
            exit 1
        }
        & git -C $root merge-base --is-ancestor $BaseSha $headSha 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Output "TOPOLOGY MISMATCH (republish): base ($BaseSha) is already an ancestor of HEAD ($headSha); pending MERGE_HEAD is unexpected."
            exit 1
        }
    } else {
        if ($headSha -ceq $BaseSha) {
            Write-Output "TOPOLOGY MISMATCH (republish): HEAD equals manifest base ($BaseSha); no published commit on branch (use -PublishMode initial)."
            exit 1
        }
        & git -C $root merge-base --is-ancestor $BaseSha $headSha 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Output "TOPOLOGY MISMATCH (republish): manifest base ($BaseSha) is not an ancestor of HEAD ($headSha) and MERGE_HEAD is absent (run base-integration first)."
            exit 1
        }
    }

    $sep = [char]0x1F
    $branchCommits = @(& git -C $root log "--format=%H%x1f%P%x1f%s" "$BaseSha..$headSha")
    if ($LASTEXITCODE -ne 0 -or $branchCommits.Count -eq 0) {
        Write-Output "TOPOLOGY MISMATCH (republish): no branch commits found in $BaseSha..$headSha."
        exit 1
    }
    foreach ($commitLine in $branchCommits) {
        $fields = $commitLine.Split($sep, 3)
        if ($fields.Count -lt 3) {
            Write-Output "TOPOLOGY MISMATCH (republish): cannot parse branch commit entry '$commitLine'."
            exit 1
        }
        $cSha = $fields[0].Trim()
        $parents = @($fields[1].Trim() -split ' ' | Where-Object { $_ })
        $subject = $fields[2]
        if ($subject.StartsWith('task-integrate WIP ')) {
            Write-Output "TOPOLOGY MISMATCH (republish): local task-integrate WIP commit ($cSha) must not be published."
            exit 1
        }
        if ($parents.Count -eq 0) {
            Write-Output "TOPOLOGY MISMATCH (republish): unexpected orphan root commit ($cSha) on branch."
            exit 1
        }
        if ($parents.Count -gt 2) {
            Write-Output "TOPOLOGY MISMATCH (republish): unexpected octopus merge commit ($cSha has $($parents.Count) parents)."
            exit 1
        }
        if ($parents.Count -eq 2) {
            $secondParent = $parents[1]
            & git -C $root merge-base --is-ancestor $secondParent $BaseSha 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Output "TOPOLOGY MISMATCH (republish): merge commit ($cSha) merges non-base parent ($secondParent) outside ancestry of base ($BaseSha)."
                exit 1
            }
        }
    }

    if ($ExpectedPublishedHead) {
        $pubSha = Resolve-CommitSha $ExpectedPublishedHead
        if (-not $pubSha) {
            Write-Output "TOPOLOGY MISMATCH (republish): -PublishedHead '$ExpectedPublishedHead' does not resolve to a commit."
            exit 1
        }
        & git -C $root merge-base --is-ancestor $pubSha $headSha 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Output "TOPOLOGY MISMATCH (republish): published head $pubSha ($ExpectedPublishedHead) is not an ancestor of HEAD ($headSha)."
            exit 1
        }
    }
}

if ($Write) {
    $baseSha = Resolve-CommitSha $Base
    if (-not $baseSha) { Write-Error "Base '$Base' does not resolve to a commit."; exit 1 }
    Assert-NoUnmergedIndex
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
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $digest = ([BitConverter]::ToString($sha256.ComputeHash($manifestBytes)) -replace '-', '').ToLowerInvariant() }
finally { $sha256.Dispose() }

$expectedLines = @(([Text.Encoding]::UTF8.GetString($manifestBytes) -split "\r?\n") | Where-Object { $_ -ne '' })
if ($expectedLines.Count -lt 2 -or -not $expectedLines[0].StartsWith($header) -or
    $expectedLines[0].Substring($header.Length) -cnotmatch '^[0-9a-f]{40,64}$') {
    Write-Error 'Invalid or outdated manifest header (expected candidate-manifest v2); regenerate it with -Write.'; exit 1
}
$baseSha = $expectedLines[0].Substring($header.Length)
$expected = @{}
foreach ($line in $expectedLines[1..($expectedLines.Count - 1)]) {
    $parts = $line.Split(' ', 3)   # mode, blob-or-DELETED, path
    if ($parts.Count -ne 3 -or -not (Test-SafeRepoRelPath $parts[2]) -or $expected.ContainsKey($parts[2])) {
        Write-Output "MANIFEST ERROR: Invalid or duplicate manifest entry: $line"
        exit 1
    }
    if ($parts[1] -eq 'DELETED') {
        if ($parts[0] -ne '-') {
            Write-Output "MANIFEST ERROR: Invalid DELETED manifest entry mode: $line"
            exit 1
        }
    } else {
        if ($parts[0] -cnotmatch '^(100644|100755|120000)$' -or $parts[1] -cnotmatch '^[0-9a-f]{40,64}$') {
            Write-Output "MANIFEST ERROR: Invalid manifest entry mode/blob: $line"
            exit 1
        }
        if (Test-SecretPath $parts[2]) {
            Write-Output "MANIFEST ERROR: Secret-like file listed in manifest: $($parts[2])"
            exit 1
        }
    }
    $expected[$parts[2]] = "$($parts[0]) $($parts[1])"
}

if ($RequireBase) {
    $requiredBaseSha = Resolve-CommitSha $RequireBase
    if (-not $requiredBaseSha) {
        Write-Output "BASE MISMATCH: required base '$RequireBase' does not resolve to a commit (manifest base $baseSha, sha256 $digest)."
        exit 1
    }
    if ($baseSha -cne $requiredBaseSha) {
        Write-Output "BASE MISMATCH: manifest base $baseSha != required base $requiredBaseSha ($RequireBase) (manifest sha256 $digest)."
        exit 1
    }
}

Assert-NoUnmergedIndex

if ($PublishMode) {
    Assert-PublishTopology -Mode $PublishMode -BaseSha $baseSha -ExpectedPublishedHead $PublishedHead
}

if ($Both) {
    $wtActual = Get-WorktreeEntries $baseSha
    $idxActual = Get-IndexEntries $baseSha
    $wtProblems = Compare-ManifestEntries $expected $wtActual
    $idxProblems = Compare-ManifestEntries $expected $idxActual
    if ($wtProblems.Count -gt 0 -or $idxProblems.Count -gt 0) {
        Write-Output "CANDIDATE MISMATCH (working tree and staged index vs manifest sha256 $digest):"
        foreach ($p in $wtProblems) { Write-Output "  [working-tree] $p" }
        foreach ($p in $idxProblems) { Write-Output "  [staged-index] $p" }
        exit 1
    }
    $suffix = ''
    if ($RequireBase) { $suffix += ", base-verified $RequireBase" }
    if ($PublishMode) { $suffix += ", publish-mode $PublishMode" }
    Write-Output "CANDIDATE MATCH: working tree and staged index == manifest sha256 $digest ($($wtActual.Count) entries, base $baseSha$suffix)"
    exit 0
}

$actual = if ($Index) { Get-IndexEntries $baseSha } else { Get-WorktreeEntries $baseSha }
$problems = Compare-ManifestEntries $expected $actual
$scope = if ($Index) { 'staged index' } else { 'working tree' }
if ($problems.Count -gt 0) {
    Write-Output "CANDIDATE MISMATCH ($scope vs manifest sha256 $digest):"
    foreach ($p in $problems) { Write-Output "  $p" }
    exit 1
}
$suffix = ''
if ($RequireBase) { $suffix += ", base-verified $RequireBase" }
if ($PublishMode) { $suffix += ", publish-mode $PublishMode" }
Write-Output "CANDIDATE MATCH: $scope == manifest sha256 $digest ($($actual.Count) entries, base $baseSha$suffix)"
exit 0
