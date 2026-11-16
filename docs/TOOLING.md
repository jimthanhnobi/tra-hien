# Codex tooling selection for Trà Hiên V2

Reviewed: 2026-11-16. External tool APIs, release behavior and permissions can change; inspect their current README before installation.

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

Use the four small Trà Hiên-specific project skills first. They depend on no package manager and do not prematurely decide the application stack.

After importing actual `docs/spec/**/*.md`:
1. Run `$spec-audit`. Resolve architecture-blocking gaps without modifying the original specs.
2. Run `$architecture-plan` and review the ADR + implementation task list.
3. Implement one task with `$implementation-slice`.
4. Review changes with `$review-gate`. Verify actual tests/CI before merge.

Codex CLI/IDE supports repo-level skills at `.agents/skills/<skill-name>/SKILL.md`. If skills do not appear, restart Codex or inspect its skills listing.

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

## What we intentionally have NOT set up

- No .NET or Next.js scaffold; no database tables; no guessed business logic.
- No mandatory external tool or unreviewed third-party workflow.
- No automatic PR approval/merge or generated CI claims.
- No real application tests until the application exists.

## Selection rule

Prefer **one** primary spec/planning process; do not combine every workflow indiscriminately. Additional tools should add genuinely independent checks (static lint, test, security scan or review), not duplicate documents for the sake of it.
