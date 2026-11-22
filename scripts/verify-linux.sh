#!/usr/bin/env bash
# CI-equivalent local verification inside infra/Dockerfile.tools. Invoke via scripts/verify-linux.ps1.
# Modes:
#   backend  (default) = CI job "Backend and API contract": classifier tests, locked restore,
#            build -warnaserror, format whitespace, all backend tests on PostgreSQL 17 Testcontainers,
#            zero-skip check, idempotent migration SQL, check:api.
#   api      = regenerate OpenAPI + TypeScript client from the real API; outputs copied to /out/api-client.
#   frontend = CI job "Frontend verification": frozen install, lint, typecheck, tests, builds.
#   full     = backend + frontend.
# Env: TEST_FILTER = dotnet test --filter expression for focused runs (full suite still required before G4).
# The worktree is never mounted: /src is a read-only, filtered snapshot made by verify-linux.ps1
# (tracked + untracked-not-ignored files only) and is copied to /work before anything runs.
set -euo pipefail
exec 2>&1

MODE="${1:-backend}"
TEST_FILTER="${TEST_FILTER:-}"
SRC=/src
WORK=/work
OUT=/out
PNPM_STORE="${PNPM_STORE:-/cache/pnpm-store}"
case "$MODE" in
  backend|api|frontend|full) ;;
  *) echo "Usage: verify-linux.sh backend|api|frontend|full"; exit 2 ;;
esac

T0=$(date +%s)
step() { printf '\n=== [%4ss] %s ===\n' "$(( $(date +%s) - T0 ))" "$*"; }
pnpm_install() { corepack pnpm install --frozen-lockfile --store-dir "$PNPM_STORE" "$@"; }

# Docker Desktop maps host.docker.internal to both IPv4 and IPv6; this container has no IPv6 route,
# so Testcontainers connections fail intermittently with "Network is unreachable". Keep the host NAME
# (application guards allowlist it) but drop its IPv6 entries from this throwaway container's /etc/hosts.
if grep -qE '^[0-9a-fA-F:]*:[0-9a-fA-F:]*[[:space:]].*host\.docker\.internal' /etc/hosts 2>/dev/null; then
  grep -vE '^[0-9a-fA-F:]*:[0-9a-fA-F:]*[[:space:]].*host\.docker\.internal' /etc/hosts > /tmp/hosts.ipv4
  cat /tmp/hosts.ipv4 > /etc/hosts
fi
echo "Testcontainers host: ${TESTCONTAINERS_HOST_OVERRIDE:-<default>} -> $(getent hosts host.docker.internal | awk '{ print $1 }' | tr '\n' ' ')"

summarize_trx() {
  python3 - "$1" <<'PY'
import collections, glob, sys, xml.etree.ElementTree as ET
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
files = sorted(glob.glob(sys.argv[1] + "/**/*.trx", recursive=True))
if not files:
    print("No TRX files produced."); sys.exit(0)
by_class = collections.defaultdict(float)
for f in files:
    root = ET.parse(f).getroot()
    c = root.find(".//t:Counters", ns).attrib
    print(f"{f.rsplit('/', 1)[-1]}: total={c['total']} executed={c['executed']} passed={c['passed']} failed={c['failed']}")
    for r in root.findall(".//t:UnitTestResult", ns):
        h, m, s = r.attrib.get("duration", "0:0:0").split(":")
        by_class[r.attrib.get("testName", "").split("(")[0].rsplit(".", 1)[0]] += int(h) * 3600 + int(m) * 60 + float(s)
print("--- slowest test classes (sum of durations) ---")
for name, sec in sorted(by_class.items(), key=lambda x: -x[1])[:10]:
    print(f"  {sec:7.1f}s  {name}")
PY
}

backend() {
  step "CI scope classifier regression"
  python3 scripts/test-ci-fastpath.py
  step "Locked restore, build -warnaserror, format whitespace"
  dotnet restore backend/TeaPremium.sln --locked-mode
  dotnet build backend/TeaPremium.sln --no-restore -warnaserror
  dotnet format whitespace backend/TeaPremium.sln --verify-no-changes --no-restore
  step "Backend tests on PostgreSQL 17 Testcontainers${TEST_FILTER:+ (filter: $TEST_FILTER)}"
  for exe in ffmpeg ffprobe docker; do
    command -v "$exe" >/dev/null || { echo "FAIL: required executable '$exe' is missing in the tools image"; exit 1; }
  done
  local results="$WORK/artifacts/test-results" rc=0
  local args=(backend/TeaPremium.sln --no-build --no-restore --logger trx --results-directory "$results")
  if [ -n "$TEST_FILTER" ]; then args+=(--filter "$TEST_FILTER"); fi
  dotnet test "${args[@]}" || rc=$?
  summarize_trx "$results" || true
  mkdir -p "$OUT/test-results"
  cp "$results"/*.trx "$OUT/test-results/" 2>/dev/null || true
  if [ "$rc" -ne 0 ]; then echo "FAIL: dotnet test exited with $rc"; exit "$rc"; fi
  if [ -z "$TEST_FILTER" ]; then
    step "Reject skipped or empty test runs"
    pwsh -NoProfile -File scripts/check-test-results.ps1 -Directory "$results"
  else
    echo "FOCUSED RUN: zero-skip check NOT RUN; run the full suite before READY_FOR_LOCAL_G4."
  fi
  step "Idempotent migration SQL without a database"
  pwsh -NoProfile -File scripts/migrations.ps1
  cp artifacts/migrations.sql "$OUT/"
  step "API contract matches the committed client"
  pnpm_install --filter '@teapremium/api-client...'
  corepack pnpm check:api
}

api() {
  step "Locked restore of the API project"
  dotnet restore backend/src/TeaPremium.Api --locked-mode
  step "Generate OpenAPI and TypeScript client from the real API"
  pnpm_install --filter '@teapremium/api-client...'
  corepack pnpm generate:api
  corepack pnpm check:api
  mkdir -p "$OUT/api-client"
  cp packages/api-client/openapi.json "$OUT/api-client/openapi.json"
  cp packages/api-client/src/generated/schema.ts "$OUT/api-client/schema.ts"
  sha256sum "$OUT/api-client/openapi.json" "$OUT/api-client/schema.ts"
  if cmp -s packages/api-client/openapi.json "$SRC/packages/api-client/openapi.json" \
    && cmp -s packages/api-client/src/generated/schema.ts "$SRC/packages/api-client/src/generated/schema.ts"; then
    echo "GENERATED CONTRACT: identical to the worktree."
  else
    echo "GENERATED CONTRACT: differs from the worktree (use -WriteBack to apply)."
  fi
}

frontend() {
  step "Frozen workspace install"
  pnpm_install
  step "Lint and typecheck"
  corepack pnpm lint
  corepack pnpm typecheck
  step "Playwright Chromium and frontend tests"
  corepack pnpm --filter @teapremium/admin exec playwright install --with-deps chromium
  corepack pnpm test
  step "Production builds"
  corepack pnpm build
}

step "Snapshot read-only source into $WORK (mode=$MODE)"
mkdir -p "$WORK" "$OUT"
# /src is already a filtered host snapshot (no git-ignored files); keep the excludes as defense in depth.
tar -C "$SRC" \
  --exclude=.git \
  --exclude=artifacts \
  --exclude=node_modules \
  --exclude='backend/src/*/bin' \
  --exclude='backend/src/*/obj' \
  --exclude='backend/tests/*/bin' \
  --exclude='backend/tests/*/obj' \
  --exclude='apps/*/.next' \
  -cf - . | tar -C "$WORK" -xf -
cd "$WORK"
secrets="$(find . -type f \( -name '.env' -o -name '.env.*' \) ! -name '*.example' -print)"
if [ -n "$secrets" ]; then echo "FAIL: environment files must not enter the container:"; echo "$secrets"; exit 1; fi

case "$MODE" in
  backend) backend ;;
  api) api ;;
  frontend) frontend ;;
  full) backend; frontend ;;
esac
step "ALL PASSED (mode=$MODE${TEST_FILTER:+, focused})"
