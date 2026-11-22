<#
.SYNOPSIS
  Create an isolated worktree for exactly one roadmap task from fresh origin/main (or an explicit -Base).
.DESCRIPTION
  Lets Codex and Antigravity work in parallel without sharing a writable checkout: one writer tool
  per worktree; reviewers read the same worktree read-only. The worktree is created next to the
  repository as tra-hien-<task> with a new branch that does not track origin/main.
  git fetch/worktree writes .git, so sandboxed agents need bypass/approval for this script.
.EXAMPLE
  ./scripts/new-task-worktree.ps1 -Task p1-10 -Branch feature/p1-10-inventory-alerts
.EXAMPLE
  ./scripts/new-task-worktree.ps1 -Task p1-13 -Branch feature/p1-13-admin-inventory -Install
  Also runs a frozen pnpm install for local typecheck/lint in the new worktree.
.EXAMPLE
  ./scripts/new-task-worktree.ps1 -Task p1-10 -Branch feature/p1-10-inventory-alerts -Base chore/agent-workflow-tuning
  Start from another reviewed branch that is not merged yet (record it; integrate main later).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{1,40}$')][string]$Task,
    [Parameter(Mandatory)][ValidatePattern('^(feature|fix|chore|docs|planning)/[a-z0-9][a-z0-9.-]{1,80}$')][string]$Branch,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._/-]{0,120}$')][string]$Base = 'origin/main',
    [switch]$Install
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$path = Join-Path (Split-Path -Parent $repo) "tra-hien-$Task"

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { Write-Error "git $($args -join ' ') failed."; exit 1 }
}

if (Test-Path -LiteralPath $path) {
    Write-Error "$path already exists. Reuse it, or remove it safely with 'git worktree remove' after its work is merged."
    exit 1
}
& git -C $repo show-ref --verify --quiet "refs/heads/$Branch"
if ($LASTEXITCODE -eq 0) { Write-Error "Branch $Branch already exists; choose another name or reuse its worktree."; exit 1 }

Invoke-Git -C $repo fetch origin --prune
$baseSha = (git -C $repo rev-parse --verify "$Base^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or -not $baseSha) { Write-Error "Base '$Base' does not resolve to a commit."; exit 1 }
Invoke-Git -C $repo worktree add --no-track -b $Branch $path $baseSha

if ($Install) {
    Push-Location -LiteralPath $path
    corepack pnpm install --frozen-lockfile
    $rc = $LASTEXITCODE
    Pop-Location
    if ($rc -ne 0) { Write-Error 'pnpm install failed in the new worktree.'; exit $rc }
}

Write-Output "WORKTREE: $path"
Write-Output "BRANCH:   $Branch (no upstream; publish with git push -u origin $Branch)"
Write-Output "BASE:     $Base $baseSha"
Write-Output 'NEXT:     open Codex or Antigravity at this path; one writer tool per worktree.'
