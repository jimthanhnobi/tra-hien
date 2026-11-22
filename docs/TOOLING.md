# Agent tooling selection for Trà Hiên V2 (Codex + Antigravity)

Reviewed: 2026-11-22 (agent workflow tuning). External tool APIs, release behavior and permissions can change; inspect their current README before installation.

| Tool / repository | Role | Adoption |
| --- | --- | --- |
| [Trà Hiên project skills](../.agents/skills) | Spec audit, architecture planning, one-slice implementation, skeptical review, base integration, publish | **Already committed**; Codex and Antigravity both discover repository skills from `.agents/skills` |
| [verify-linux.ps1](../scripts/verify-linux.ps1) + [Dockerfile.tools](../infra/Dockerfile.tools) | CI-equivalent backend/API/frontend verification in Linux Docker (Windows blocks the OpenAPI generator) | **Committed**; required evidence path for G3/G4 |
| [new-task-worktree.ps1](../scripts/new-task-worktree.ps1) | One isolated worktree per roadmap task for parallel Codex/Antigravity work | **Committed** |
| [candidate-manifest.ps1](../scripts/candidate-manifest.ps1) | Content fingerprint (git mode + blob ID) of a task candidate, verified by G4 and before commit, for both tiers | **Committed**; required by the skills |
| [task-integrate.ps1](../scripts/task-integrate.ps1) | Base integration of an unpublished task without the shared stash: task-private recovery ref, rebase, compare-and-delete | **Committed**; used by `base-integration` |
| [test-agent-tooling.ps1](../scripts/test-agent-tooling.ps1) | Self-test of the tooling scripts in a throwaway repo with a fake Docker | **Committed**; run after changing any tooling script |
| [github/spec-kit](https://github.com/github/spec-kit) | Structured spec -> clarify -> plan -> tasks -> analyze -> implement -> converge | Optional after importing real V2 specs |
| [obra/superpowers](https://github.com/obra/superpowers) | Methodology for planning, TDD, execution, debugging, and review | Optional Codex plugin |
| [openai/plugins](https://github.com/openai/plugins) | Current official plugin examples/marketplace, including Superpowers and CodeRabbit | Discovery source, not copied into this repo |
| [CodeRabbit Codex plugin](https://github.com/openai/plugins/tree/main/plugins/coderabbit) | Independent AI review of local changes | Optional; evaluate data-sharing and access permissions |
| [reviewdog/reviewdog](https://github.com/reviewdog/reviewdog) | Annotate PRs with linter/static-analysis output | After framework/lint tools are chosen |
| [openai/skills](https://github.com/openai/skills) | Older Codex skills catalog | Deprecated; **do not use as main integration baseline** |

## Recommended now

Use the six committed Trà Hiên project skills and existing G1/G2 evidence, not an additional methodology stack. The architecture and roadmap are already specified. Check changes since the audited spec SHA before each task.

1. Full `spec-audit` / `architecture-plan` at baseline or after material spec/architecture changes; for routine tasks, validate only affected requirements and dependencies.
2. Implement exactly one approved `P*-*` task with `implementation-slice` in one main thread and one dedicated worktree. Subagents are optional, not required by a skill.
3. Perform G4 with `review-gate` in an independent session, preferably on the other tool or model vendor; verify actual tests/CI and keep owner-only merge.
4. Use `base-integration` when `main` moves and `publish-pr` only after G4 PASS and owner permission.
5. Prefer small evidence summaries over repeating full spec text or spawning multiple agents to inspect the same files.

Codex invokes skills as `$name`; Antigravity by skill name or path. If skills do not appear, verify the repository root/trust and restart the tool. See [WORKFLOW.md](WORKFLOW.md) and [CODEX_USAGE.md](CODEX_USAGE.md) for the process and prompts.

## Optional: Spec Kit in this existing repository

First commit your working tree or create a setup branch. From the repository root, after installing Python 3.11+, uv and Codex CLI:

~~~powershell
uv tool install specify-cli
specify init --here --force --integration codex
git status
git diff
~~~

**Warning:** `--force` may replace conflicting managed files. Review the diff, do not automatically commit or merge. Spec Kit adds a `.specify/` tree and `.agents/skills/speckit-*/`. Its generated feature specs must refer back to the authoritative `docs/spec/` files; they do not supersede them.

Invoke the installed Spec Kit skills **in Codex chat** (not Powershell), with e.g. `$speckit-constitution`, `$speckit-clarify`, `$speckit-plan`, `$speckit-tasks`, `$speckit-analyze`, `$speckit-implement`, and `$speckit-converge`. Choose the subset appropriate to the feature and current version.

## Optional: Superpowers and CodeRabbit

In supported Codex installations, open `/plugins` and search for `Superpowers` or `CodeRabbit`, review the permissions and install deliberately. Their presence is **not asserted or required**. Superpowers is a methodology layer; our Trà Hiên `AGENTS.md` still defines spec authority, scope and approval gates. Do not rely on an external AI reviewer as the only quality check.

## Current repo snapshot (22/11/2026)

- P0-01…P0-12 and P1 tasks up to P1-14 (except P1-10 and P1-13) are merged; see `git log` and `docs/plans/` for evidence.
- GitHub Actions CI (P0-11) runs four jobs on PRs to `main` and pushes to `main`. Local runs, including `verify-linux.ps1`, are never described as CI verification.
- `.codex/` (Codex-only subagents/config) is optional; Antigravity ignores it and uses `AGENTS.md` plus the shared skills.
- No mandatory external tooling or auto PR approval/merge. External tools remain optional and must not override authoritative V2 specs.

## Selection rule

Prefer **one** primary spec/planning process; do not combine every workflow indiscriminately. Additional tools should add genuinely independent checks (static lint, test, security scan or review), not duplicate documents for the sake of it.
