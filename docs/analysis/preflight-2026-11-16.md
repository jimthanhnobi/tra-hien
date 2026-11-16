# Preflight review – spec update 2026-11-16

Status: **PRELIMINARY, not a complete specification audit**. Source commit: `ba0bb64e0ff755b85fa8189cbad68a61aa56f09c`. Reviewed Markdown headings and key contracts across 00–09 plus schema declarations. No application was run and SQL was not executed in PostgreSQL.

## Confirmed spec-to-workflow mismatches resolved in tooling

1. Old `spec-audit` generated `REQ-###` despite spec already defining `FR-*`, `BR-*`, `NFR-*`, `AC-*`, `DEC-*` in `00-README.md §2`. The skill now retains source IDs.
2. Old `architecture-plan` created another architecture/backlog, but `00-README.md §6` records DEC-01..DEC-24 and `07-roadmap.md` already has P0–P6 tasks. The skill now reviews conformance and task readiness.
3. Old workflow told agents to exclude a placeholder README; the real `00-README.md` is authoritative and must be read, as must `09-seo.md` and `schema.sql`.
4. Old implementation skill cited a made-up `TASK-XXX` backlog; now it scopes to authoritative `P*-*` tasks.

## Issues requiring detailed audit / approval before relevant implementation

| Priority | Evidence in original specs | Finding / action | Relevant scope |
| --- | --- | --- | --- |
| P1, task-scoped | `05-api.md §1` CSRF convention; `§2` guest cart/checkout; `§3` account/auth/csrf; `§4.1` admin/auth/csrf | The guest storefront endpoints require a CSRF token, but the API list does not specify a guest `store/auth/csrf` endpoint or an equivalent issuance mechanism. Confirm the exact acquisition/renewal contract, including guest first visit. | Before P3-01/P3-02 |
| P1, task-scoped | `04-business-rules.md BR-CAT-08`, `09-seo.md BR-SEO-03`, `05-api.md §2, §4.7` | Redirect rules include products/categories/collections/materials/artisans/articles/pages but omit author and landing page slug changes, although their SEO URLs and edit APIs exist. Define redirect behavior, including duplicate source-path conflicts and changed slugs. | Before P3-14 / P3-17 |
| P1, external verification | `09-seo.md BR-SEO-80` and `BR-SEO-71` | Merchant feed unconditionally sets `g:brand` to the shop name while product JSON-LD says no fake brand. Google's brand guidance requires the real product brand; shop brand is appropriate only in certain merchant-as-manufacturer/private-label cases. Define product-brand mapping and identifier_exists handling; verify Google Merchant guidance before feed implementation. | Before P3-15 |
| P2, verification | `03-database.md §1` declares 78 tables / 11 schemas and says SQL was tested; `schema.sql` contains 78 CREATE TABLE / 11 CREATE SCHEMA declarations | Counts match the documented claim but this review has NOT executed the DDL nor proved migration parity. Verify SQL through real PostgreSQL 17 and subsequent EF migrations. | P0-04/P0-10/P1-01 |
| P2, capacity risk | `06-infrastructure.md §1, §3`, `01-requirements.md NFR-01,NFR-13` | 2 vCPU/4GB is an unverified capacity/cost assumption for PostgreSQL 17, API background jobs, two Next apps, and image resize. Test memory/TTFB with realistic load and account for backup overhead before procurement. | Before P3-10 |
| P2, owner confirmation | `00-README.md §5–6` | Q1–Q8 are documented open items. Respect the stated deferral to P3 unless a particular task depends on one. Validate material DEC business/brand claims with owner; do not silently treat AI-generated text as confirmed real-world approvals. | Before affected task/go-live |

## First practical gate

**Recommendation:** Run full `$spec-audit` and then `$architecture-plan` conformance review. If no P0-01 blocking issue is found and the project owner approves, start `P0-01` only (solution scaffold; no business DB/domain). Do not start P3 contracts or merchant feed yet.

Do not edit `docs/spec/` based on this preliminary list without explicit user review.
