# Codex tooling selection for Trà Hiên V2

Reviewed: 2026-11-17 (workflow housekeeping). External tool APIs, release behavior and permissions can change; inspect their current README before installation.

| Tool / repository | Role | Adoption |
| --- | --- | --- |
| [Trà Hiên project skills](../.agents/skills) | Spec audit, architecture planning, one-slice implementation, skeptical review | **Already committed**; Codex discovers repository skills from `.agents/skills` |
| [github/spec-kit](https://github.com/github/spec-kit) | Structured spec -> clarify -> plan -> tasks -> analyze -> implement -> converge | Optional after importing real V2 specs |
| [obra/superpowers](https://github.com/obra/superpowers) | Methodology for planning, TDD, execution, debugging, and review | Optional Codex plugin |
| [openai/plugins](https://github.com/openai/plugins) | Current official plugin examples/marketplace, including Superpowers and CodeRabbit | Discovery source, not copied into this repo |
| [CodeRabbit Codex plugin](https://github.com/openai/plugins/tree/main/plugins/coderabbit) | Independent AI review of local changes | Optional; evaluate data-sharing and access permissions |
| [reviewdog/reviewdog](https://github.com/reviewdog/reviewdog) | Annotate PRs with linter/static-analysis output | After framework/lint tools are chosen |
| [openai/skills](https://github.com/openai/skills) | Older Codex skills catalog | Deprecated; **do not use as main integration baseline** |

## Recommended now

Use the four committed Trà Hiên project skills and existing G1/G2 evidence, not an additional methodology stack. The architecture and roadmap are already specified. Check changes since the audited spec SHA before each task.

1. Full `$spec-audit` / `$architecture-plan` at baseline or after material spec/architecture changes; for routine tasks, validate only affected requirements and dependencies.
2. Implement exactly one approved `P*-*` task with `$implementation-slice` in the main Codex thread by default. Subagents are optional, not required by a skill.
3. Perform G4 with `$review-gate` in an independent reviewer session; verify actual tests/CI and keep owner-only merge.
4. Prefer small evidence summaries over repeating full spec text or spawning multiple agents to inspect the same files.

Codex CLI/IDE discovers project skills under `.agents/skills/`. If skills do not appear, verify repo root/trust or restart Codex. See `docs/WORKFLOW.md` and `docs/CODEX_USAGE.md` for current prompts.

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

## Current repo snapshot (17/11/2026)

- The .NET 10 solution, Next.js admin/store scaffolds, PostgreSQL 17 platform schema and backend test projects already exist. P0-06 outbox/jobs is merged.
- ArchitectureTests, PostgreSQL 17 Testcontainers and migration tests already exist; P0-10 must strengthen missing coverage, not rebuild them.
- GitHub Actions CI is not configured yet at this snapshot (planned P0-11); never describe local tests as CI verification.
- No mandatory external tooling or auto PR approval/merge. External tools remain optional and must not override authoritative V2 specs.

## Selection rule

Prefer **one** primary spec/planning process; do not combine every workflow indiscriminately. Additional tools should add genuinely independent checks (static lint, test, security scan or review), not duplicate documents for the sake of it.
