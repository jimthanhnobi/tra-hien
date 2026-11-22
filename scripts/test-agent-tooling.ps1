<#
.SYNOPSIS
  Self-test for the agent tooling scripts (verify-linux.ps1 wrapper logic and candidate-manifest.ps1).
.DESCRIPTION
  Runs entirely in a throwaway git repository under the system temp directory with a fake `docker`
  command first on PATH, so it never touches the real worktree, its index or the Docker engine.
  Covers: filtered snapshot excludes git-ignored files, socket mounted only for backend/full, run by
  image ID, mode normalisation, exit-code propagation, -WriteBack failure handling, and manifest
  detection of changed/extra/missing content in the working tree and the staged index.
.EXAMPLE
  ./scripts/test-agent-tooling.ps1
#>
$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$id = [guid]::NewGuid().ToString('N').Substring(0, 12)
$tmp = Join-Path ([IO.Path]::GetTempPath()) "tra-hien-tooling-$id"
$bin = Join-Path ([IO.Path]::GetTempPath()) "tra-hien-tooling-$id-bin"
$fakeLog = Join-Path $bin 'docker-calls.log'
$originalPath = $env:PATH
$originalDockerfile = $env:FAKE_DOCKERFILE
$script:failures = 0
$script:passes = 0

function Assert([bool]$Condition, [string]$Name) {
    if ($Condition) { $script:passes++; Write-Output "PASS  $Name"; return }
    $script:failures++
    Write-Output "FAIL  $Name"
    if ($script:last) {
        $text = "rc=$($script:last.Rc)`n$($script:last.Output)`n--- docker calls ---`n$($script:last.Calls)"
        Write-Output ($text.Substring(0, [Math]::Min(1500, $text.Length)) -replace '(?m)^', '      | ')
    }
}
function Write-Text([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Invoke-Tool([string]$Script, [string[]]$Arguments, [hashtable]$Environment = @{}, [string]$Root = $tmp) {
    foreach ($key in $Environment.Keys) { Set-Item "env:$key" $Environment[$key] }
    Remove-Item -LiteralPath $fakeLog -ErrorAction SilentlyContinue
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts/$Script") @Arguments 2>&1 | Out-String
    $rc = $LASTEXITCODE
    foreach ($key in $Environment.Keys) { Remove-Item "env:$key" -ErrorAction SilentlyContinue }
    $calls = if (Test-Path -LiteralPath $fakeLog) { Get-Content -LiteralPath $fakeLog -Raw } else { '' }
    $script:last = [pscustomobject]@{ Rc = $rc; Output = $output; Calls = $calls }
    return $script:last
}
function Invoke-TmpGit { & git -c user.name=selftest -c 'user.email=selftest@example.invalid' -C $tmp @args 2>&1 | Out-Null }
function Invoke-GitAt([string]$Dir) { & git -c user.name=selftest -c 'user.email=selftest@example.invalid' -C $Dir @args 2>&1 | Out-Null }

try {
    # Throwaway repository with an ignored secret and committed API outputs.
    foreach ($name in 'verify-linux.ps1', 'verify-linux.sh', 'candidate-manifest.ps1', 'task-integrate.ps1') {
        Write-Text (Join-Path $tmp "scripts/$name") ([IO.File]::ReadAllText((Join-Path $repoRoot "scripts/$name")))
    }
    Write-Text (Join-Path $tmp 'infra/Dockerfile.tools') ([IO.File]::ReadAllText((Join-Path $repoRoot 'infra/Dockerfile.tools')))
    Write-Text (Join-Path $tmp '.gitignore') ".env`nartifacts/`n"
    Write-Text (Join-Path $tmp 'infra/.env') "SECRET=do-not-copy`n"
    Write-Text (Join-Path $tmp 'packages/api-client/openapi.json') "ORIGINAL-OPENAPI`n"
    Write-Text (Join-Path $tmp 'packages/api-client/src/generated/schema.ts') "ORIGINAL-SCHEMA`n"
    Invoke-TmpGit init -q
    Invoke-TmpGit add -A
    Invoke-TmpGit commit -q -m base

    # Fake docker: records arguments, inspects the mounted snapshot, optionally writes API outputs.
    Write-Text (Join-Path $bin 'docker.cmd') "@powershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0fake-docker.ps1`" %*`r`n"
    Write-Text (Join-Path $bin 'fake-docker.ps1') @'
$log = Join-Path $PSScriptRoot 'docker-calls.log'
Add-Content -LiteralPath $log -Value ("ARGS " + ($args -join ' | '))
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
    $openapi = Join-Path $tmp 'packages/api-client/openapi.json'

    # verify-linux.ps1
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api')
    Assert ($r.Rc -eq 3) 'api: missing generated outputs fail with exit 3'
    Assert ($r.Output -match 'RESULT=FAIL') 'api: missing outputs report RESULT=FAIL'
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
    Assert (([IO.File]::ReadAllText($openapi)).Trim() -eq 'FAKE-OPENAPI') 'api -WriteBack: target written'
    Assert ($r.Output -match 'hash verified') 'api -WriteBack: destination hash verified'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'Backend')
    Assert ($r.Rc -eq 0) 'Mode Backend (mixed case) accepted'
    Assert ($r.Calls -match '\| backend\r?\n') 'mode normalised to lowercase for the Linux script'
    Assert ($r.Calls -match 'docker\.sock') 'backend: Docker socket mounted'

    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'backend') @{ FAKE_RUN_RC = '37' }
    Assert ($r.Rc -eq 37) 'container exit code propagated (37)'

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
    $r = Invoke-Tool 'candidate-manifest.ps1' @('-Verify', $manifest)
    Assert ($r.Rc -eq 0 -and $r.Output -match 'CANDIDATE MATCH') 'verify: unchanged working tree matches'
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'api', '-Manifest', $manifest) @{ FAKE_OUTPUTS = '1' }
    Assert ($r.Rc -eq 0 -and $r.Output -match 'manifest-check: CANDIDATE MATCH') 'verify-linux -Manifest: matching candidate runs and logs manifest'
    Assert ($r.Output -match 'source=manifest objects') 'verify-linux -Manifest: snapshot built from manifest git objects'
    Assert ($r.Calls -match 'OPENAPI_IN_SNAPSHOT FAKE-OPENAPI' -and $r.Calls -match 'NEW_IN_SNAPSHOT True') 'verify-linux -Manifest: snapshot holds the reviewed modified and untracked content'
    Assert ($r.Calls -match 'ENV_IN_SNAPSHOT False') 'verify-linux -Manifest: ignored infra/.env not in snapshot'

    $reviewedDockerfileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $env:FAKE_DOCKERFILE).Hash.ToLowerInvariant()
    $r = Invoke-Tool 'verify-linux.ps1' @('-Mode', 'frontend', '-Rebuild', '-Manifest', $manifest) @{ FAKE_EDIT_LIVE_DOCKERFILE = '1' }
    Assert ($r.Rc -eq 0 -and $r.Calls -match "BUILT_DOCKERFILE_SHA256 $reviewedDockerfileHash") 'verify-linux -Manifest: build uses reviewed recipe while live Dockerfile changes'
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

    # task-integrate.ps1: per-task recovery ref, rebase of the unpublished branch, shared refs/stash untouched.
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
} finally {
    $env:PATH = $originalPath
    $env:FAKE_DOCKERFILE = $originalDockerfile
    foreach ($w in "$tmp-wtA", "$tmp-wtB", "$tmp-wtN") { Remove-Item -LiteralPath $w -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $bin -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "test-agent-tooling: $script:passes passed, $script:failures failed"
exit $script:failures
