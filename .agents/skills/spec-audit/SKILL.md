---
name: spec-audit
description: Audit all user-provided Trà Hiên V2 Markdown specifications for requirements, ambiguities, contradictions, dependencies, missing behavior, security/SEO/testing gaps, and requirement traceability. Use before architecture and whenever docs/spec changes; never implement code in this skill.
---

# Spec audit

1. First read repository `AGENTS.md` and enumerate all `docs/spec/**/*.md` excluding only `docs/spec/README.md`. Recursively inspect linked local spec files where applicable. Do not infer that the spec has arrived if only placeholder exists.
2. If real specs are missing: report BLOCKED: waiting for specs. Do not invent features, write empty business claims, or scaffold the app.
3. Build a source inventory by file and heading/line. Extract concise requirements with stable `REQ-###` IDs; keep a mapping back to authoritative path/heading. Distinguish explicit vs interpreted requirements.
4. Cross-check across files. Flag conflicting data types, lifecycle rules, auth/roles, public vs admin behavior, SEO/indexing, lead attribution/consent, reporting, integrations, and nonfunctional constraints **only if relevant to the documents**.
5. For each unclear point log source, ambiguity, business impact, potential options, recommendation labeled PROPOSAL (not fact), risk, and status OPEN/BLOCKER/RESOLVED.
6. For each requirement propose objective Given/When/Then acceptance criteria and happy, negative, boundary, security, and failure scenarios. Do not assume expected values that the spec does not provide.
7. Deliver `docs/analysis/spec-inventory.md`, `docs/analysis/spec-gaps.md`, `docs/analysis/traceability.md`. Keep `docs/spec/` unchanged.
8. Close with: number of source files read, coverage summary, highest-risk blockers, decision list, and specific next step. Do not mark G1 approved yourself.
