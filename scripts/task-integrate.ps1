<#
.SYNOPSIS
  Race-free base integration for an unpublished task branch with an uncommitted candidate.
.DESCRIPTION
  Never uses the shared refs/stash (all worktrees share it, so position-based apply/drop can hit
  another worktree's entry). Instead:
    start  : commit the whole candidate (tracked, deleted, untracked-not-ignored) as a local WIP commit,
             pin it under the task-private ref refs/task-integration/<task> (atomic create-if-absent),
             rebase the unpublished branch onto -Onto, then finish automatically when clean.
    finish : after conflicts are resolved and staged (`git add`), continue the rebase if still active and
             undo only the WIP commit with `git reset --mixed HEAD^` (worktree untouched): the integrated
              candidate is uncommitted again. New regular files are untracked again, while entries
              with a changed Git mode stay staged so Windows can represent those modes.
    drop   : after finish + verification, delete the recovery ref only if it still points at the recorded
             WIP SHA (atomic compare-and-delete via `git update-ref -d <ref> <old>`); otherwise keep it.
    show   : print the recorded state.
  Only the task branch and the task's own ref change. The WIP commit uses a fixed local identity, forces
  GIT_EDITOR=true, skips hooks and is never pushed. Recovery before drop:
    git restore --source=refs/task-integration/<task> --worktree -- .
  Published branches must use `git merge --no-ff --no-commit` instead (see the base-integration skill).
.EXAMPLE
  ./scripts/task-integrate.ps1 -Task p1-13 -Action start -Onto origin/main
.EXAMPLE
  git add <resolved files>; ./scripts/task-integrate.ps1 -Task p1-13 -Action finish
.EXAMPLE
  ./scripts/task-integrate.ps1 -Task p1-13 -Action drop   # only after the integrated candidate is verified
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{1,40}$')][string]$Task,
    [Parameter(Mandatory)][ValidateSet('start', 'finish', 'drop', 'show')][string]$Action,
    [string]$Onto = 'origin/main'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$ref = "refs/task-integration/$Task"
$stateDir = Join-Path $root "artifacts/$Task/pre-integration"
$state = Join-Path $stateDir 'integration.txt'
$marker = "task-integrate WIP $Task (local only, never push)"
$identity = @(
    '-c', 'user.name=task-integrate',
    '-c', 'user.email=task-integrate@localhost.invalid',
    '-c', 'core.editor=true',
    '-c', 'sequence.editor=true'
)

$previousGitEditor = $env:GIT_EDITOR
$env:GIT_EDITOR = 'true'

function Restore-Env {
    if ($null -eq $previousGitEditor) { Remove-Item Env:GIT_EDITOR -ErrorAction SilentlyContinue }
    else { $env:GIT_EDITOR = $previousGitEditor }
}
function Fail([string]$Message, [int]$Code = 1) { Restore-Env; Write-Output "FAIL: $Message"; exit $Code }
function Get-Sha([string]$Rev) {
    $out = @(& git -C $root rev-parse --verify --quiet "$Rev^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $out.Count -eq 0) { return '' }
    return "$($out[0])".Trim()
}
function Get-RefSha {
    $out = @(& git -C $root rev-parse --verify --quiet $ref 2>$null)
    if ($LASTEXITCODE -ne 0 -or $out.Count -eq 0) { return '' }
    return "$($out[0])".Trim()
}
function Get-InProgress {
    foreach ($name in 'rebase-merge', 'rebase-apply', 'MERGE_HEAD') {
        $path = "$(& git -C $root rev-parse --git-path $name)".Trim()
        if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $root $path }
        if (Test-Path -LiteralPath $path) { return $name }
    }
    return ''
}
function Test-SecretPath([string]$Path) {
    $normalized = $Path -replace '\\', '/'
    $leaf = ($normalized -split '/')[-1]
    if ($leaf -match '^\.env(\..+)?$' -and $leaf -notmatch '\.example$') { return $true }
    if ($leaf -match '\.(pfx|p12|pem|key|secret)$') { return $true }
    return ($normalized -match '(^|/)secrets/')
}
function Read-State {
    if (-not (Test-Path -LiteralPath $state -PathType Leaf)) { Fail "no integration state for $Task ($state)" }
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($state)) { $i = $line.IndexOf('='); if ($i -gt 0) { $values[$line.Substring(0, $i)] = $line.Substring($i + 1) } }
    $wip = $values['wip']
    if ($wip -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or (Get-Sha $wip) -cne $wip) {
        Fail "missing or invalid WIP SHA in $state"
    }
    return $values
}
function Undo-WipCommit([string]$Tip) {
    $parent = Get-Sha "$Tip^"
    if (-not $parent) { Fail "cannot resolve the parent of WIP commit $Tip" }
    $previousEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        $raw = (& git -C $root diff-tree --no-commit-id --no-renames --raw -r -z $parent $Tip) -join "`n"
        if ($LASTEXITCODE -ne 0) { Fail "cannot read mode changes from WIP commit $Tip" }
    } finally {
        [Console]::OutputEncoding = $previousEncoding
    }
    $fields = $raw.Split([char]0)
    $modePaths = @(
        for ($i = 0; $i + 1 -lt $fields.Count; $i += 2) {
            if ($fields[$i] -notmatch '^:(?<before>[0-7]{6}) (?<after>[0-7]{6}) ') {
                Fail "cannot parse mode changes from WIP commit $Tip"
            }
            $before = $Matches['before']
            $after = $Matches['after']
            if ($before -ne $after -and $after -ne '000000' -and ($before -ne '000000' -or $after -ne '100644')) {
                $fields[$i + 1]
            }
        }
    )
    & git -C $root reset --mixed -q $parent
    if ($LASTEXITCODE -ne 0) { Fail "git reset --mixed failed for WIP commit $Tip" }
    # The index is the only place Windows can reliably retain the reviewed Git mode.
    for ($start = 0; $start -lt $modePaths.Count; $start += 40) {
        $batch = $modePaths[$start..([Math]::Min($start + 39, $modePaths.Count - 1))]
        & git --literal-pathspecs -C $root reset -q $Tip -- @batch
        if ($LASTEXITCODE -ne 0) {
            & git -C $root reset --mixed -q $Tip
            Fail "could not restore mode changes from WIP commit $Tip; inspect HEAD and rerun finish if the WIP commit is still at HEAD"
        }
    }
}
function Complete-Integration {
    $values = Read-State
    $inProgress = Get-InProgress
    if ($inProgress -in 'rebase-merge', 'rebase-apply') {
        $unmerged = @(& git -c core.quotepath=false -C $root ls-files -u)
        if ($LASTEXITCODE -ne 0 -or $unmerged.Count -gt 0) {
            Fail "unresolved conflicts remain in $inProgress; resolve the files, git add them, then rerun -Action finish" 2
        }
        $stagedSecrets = @(& git -c core.quotepath=false -C $root diff --cached --name-only | Where-Object { $_ -and (Test-SecretPath $_) })
        if ($stagedSecrets.Count -gt 0) {
            Fail "secret-like files staged during conflict resolution: $($stagedSecrets -join ', ')" 4
        }
        & git -C $root @identity rebase --continue 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0 -or (Get-InProgress)) {
            Fail 'git rebase --continue did not complete cleanly; resolve any remaining conflict, git add, and rerun -Action finish' 2
        }
    } elseif ($inProgress) {
        Fail "a $inProgress is in progress; cannot finish" 2
    }
    $currentHead = Get-Sha 'HEAD'
    if ($currentHead -eq $values['onto']) {
        Write-Output "INTEGRATED: $Task onto $($values['onto']); candidate changes matched incoming base."
        Write-Output "RECOVERY:   $ref = $($values['wip']) (drop it only after verification)"
        return
    }
    $subject = "$(& git -C $root log -1 --format=%s)".Trim()
    if ($subject -ne $marker) { Fail "HEAD is not the WIP commit of $Task (found '$subject'); nothing was reset" }
    & git -C $root merge-base --is-ancestor $values['onto'] 'HEAD^'
    if ($LASTEXITCODE -ne 0) { Fail "the WIP commit is not on top of $($values['onto']); nothing was reset" }
    Undo-WipCommit $currentHead
    Write-Output "INTEGRATED: $Task onto $($values['onto']); candidate is uncommitted again (mode changes stay staged)."
    Write-Output "RECOVERY:   $ref = $($values['wip']) (drop it only after verification)"
}

try {
    switch ($Action) {
        'start' {
            $inProgress = Get-InProgress
            if ($inProgress) { Fail "a $inProgress is already in progress" }
            if (Get-RefSha) { Fail "$ref already exists; finish and drop the previous integration first" }
            $ontoSha = Get-Sha $Onto
            if (-not $ontoSha) { Fail "-Onto '$Onto' does not resolve to a commit" }
            $branch = "$(& git -C $root symbolic-ref --quiet --short HEAD 2>$null)".Trim()
            if ($LASTEXITCODE -ne 0 -or -not $branch) { Fail 'detached HEAD: run inside the task branch' }
            & git -C $root rev-parse --verify --quiet "refs/remotes/origin/$branch" 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { Fail "branch $branch is published; use 'git merge --no-ff --no-commit $Onto' (base-integration skill)" }
            $head = Get-Sha 'HEAD'
            $changed = @(& git -c core.quotepath=false -C $root diff --name-only HEAD) + @(& git -c core.quotepath=false -C $root ls-files -o --exclude-standard)
            $secrets = @($changed | Where-Object { $_ -and (Test-SecretPath $_) -and (Test-Path -LiteralPath (Join-Path $root $_)) })
            if ($secrets.Count -gt 0) { Fail "secret-like files must not be committed, even locally: $($secrets -join ', ')" 4 }
            if (@($changed | Where-Object { $_ }).Count -eq 0) {
                & git -C $root merge --ff-only -q $ontoSha
                if ($LASTEXITCODE -ne 0) { Fail "no local changes, but fast-forward to $Onto failed" }
                Write-Output "NO LOCAL CHANGES: fast-forwarded $branch to $ontoSha"
                break
            }
            & git -C $root add -A
            if ($LASTEXITCODE -ne 0) { Fail 'git add -A failed' }
            $stagedSecrets = @(& git -c core.quotepath=false -C $root diff --cached --name-only HEAD | Where-Object { $_ -and (Test-SecretPath $_) })
            if ($stagedSecrets.Count -gt 0) {
                & git -C $root reset -q
                Fail "secret-like files in index refused: $($stagedSecrets -join ', ')" 4
            }
            & git -C $root @identity commit --no-verify -q -m $marker
            if ($LASTEXITCODE -ne 0) { Fail 'WIP commit failed; candidate remains in the index' }
            $wip = Get-Sha 'HEAD'
            & git -C $root update-ref $ref $wip ('0' * $wip.Length)
            if ($LASTEXITCODE -ne 0) { Undo-WipCommit $wip; Fail "could not create $ref; WIP commit undone" }
            [IO.Directory]::CreateDirectory($stateDir) | Out-Null
            [IO.File]::WriteAllLines($state, [string[]]@("task=$Task", "branch=$branch", "base=$head", "onto=$ontoSha", "wip=$wip"))
            Write-Output "PINNED:     $ref = $wip (candidate before integration)"
            & git -C $root @identity rebase -q $ontoSha 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Output "CONFLICT: resolve the files, git add them, then run ./scripts/task-integrate.ps1 -Task $Task -Action finish"
                Restore-Env
                exit 2
            }
            Complete-Integration
        }
        'finish' { Complete-Integration }
        'drop' {
            $values = Read-State
            $inProgress = Get-InProgress
            if ($inProgress) { Fail "a $inProgress is still in progress; run -Action finish before drop" 2 }
            $subject = "$(& git -C $root log -1 --format=%s 2>$null)".Trim()
            if ($subject -eq $marker) { Fail 'WIP commit is still at HEAD; run -Action finish before drop' 2 }
            & git -C $root update-ref --no-deref -d $ref $values['wip']
            if ($LASTEXITCODE -ne 0) { Fail "$ref no longer points at $($values['wip']); kept unchanged" }
            Remove-Item -LiteralPath $state
            Write-Output "DROPPED: $ref ($($values['wip']))"
        }
        'show' {
            $values = Read-State
            $current = Get-RefSha
            Write-Output "STATE: task=$Task onto=$($values['onto']) wip=$($values['wip']) ref=$(if ($current) { $current } else { 'MISSING' })"
        }
    }
} finally {
    Restore-Env
}
exit 0
