# P0-04 — Platform database foundation: G3 implementation record

Status: **G3 completed; independent G4 GO; targeted post-G4 corrections pending re-review**. Branch: `feature/p0-04-platform-db`. Base: `61865f9364d568a845663c047ddbfb155d8edaa1` (merged PR #7). No commit, push, PR or production database operation was performed for this slice.

## Traceability and scope

| Source | P0-04 obligation and implementation |
|---|---|
| `docs/spec/00-README.md:85` (DEC-01); `07-roadmap.md:20` (P0-04) | One PostgreSQL database with a `platform` schema; `AppDbContext` registered in API and limited to nine platform tables. |
| `docs/spec/schema.sql:8–135`; `03-database.md:18` (F-01/G26) | Three extensions, `public.f_unaccent`, nine tables, 19 explicitly named constraints and eight explicit indexes. PostgreSQL also creates ten PK/UNIQUE indexes. |
| `docs/spec/02-architecture.md:105–108`; `backend/src/TeaPremium.Application/UseCases/Contracts.cs:23` | Concrete scoped `EfUnitOfWork` keeps command saves in one transaction and flushes remaining changes before a successful commit. It rolls back Result failure, exception and cancellation. |
| `docs/spec/01-requirements.md:190,201,205–206` (FR-PLT-01, NFR-04/08/09); `07-roadmap.md:36–37` (AC-P0-04) | Audit identity columns and checks are present; automated auditing and PII redaction belong to P0-05. Settings API/import belong to P0-09. AC-P0-04 remains cross-task and is **not passed** by this slice. |

Implementation files: `backend/src/TeaPremium.Infrastructure/Persistence/{AppDbContext,PlatformEntities,EfUnitOfWork,PersistenceRegistration,AppDbContextFactory}.cs`, `Persistence/Migrations/202611161624_InitialPlatform{,.Designer}.cs`, `AppDbContextModelSnapshot.cs`, API `Program.cs`, package references/lock files, and `backend/tests/TeaPremium.IntegrationTests/PlatformDatabaseTests.cs`.

## Persistence and migration contract

- `AppDbContext` maps every table to `platform`, uses snake_case, concrete PostgreSQL types, `jsonb`, UTC `timestamptz`, approved defaults, explicit PK/FK/UNIQUE/CHECK/index names, and G26 UUID/TEXT audit identity with exactly one populated value. The webhook pair uses an actual UNIQUE constraint. `data_protection_keys.id` uses PostgreSQL `serial`; `idempotency_keys` preserves `(scope, key)` ordering. UUID keys are supplied by the application.
- Migration `202611161624_InitialPlatform` creates `platform`, `unaccent`, `pg_trgm`, `btree_gist`, `public.f_unaccent(text)`, and exactly the nine approved tables. It does not seed reference data, settings or IAM. It does not create a physical `xmin` column. For a later eligible business entity, follow `03-database.md:15` and map `uint Version` with `.IsRowVersion()` to PostgreSQL's system `xmin`, then test concurrency in that entity's task.
- The API registers a scoped DbContext and `IUnitOfWork`; connection string is supplied through `ConnectionStrings:Default` (environment variable `ConnectionStrings__Default` is supported by .NET configuration). Registration does not connect or migrate on startup. EF tooling requires the variable explicitly; no credentials are stored in the repository.
- The adapter clears tracked changes after rollback so a later operation in the same scope cannot save a rejected entity. A future outbox write through the same scoped DbContext will share the command transaction; no dispatcher was added.

## Verification evidence (2026-11-16)

| Command/check | Result |
|---|---|
| `dotnet restore backend/TeaPremium.sln --force-evaluate -v:q` | PASS. Lock files updated. |
| `dotnet build backend/TeaPremium.sln --no-restore -v:q` | PASS, 0 warnings, 0 errors. |
| `dotnet test backend/TeaPremium.sln --no-build -v:q` | PASS: unit 11, architecture 6, integration 40; integration suite used disposable PostgreSQL 17 via Testcontainers. |
| `dotnet-ef migrations has-pending-model-changes` for `AppDbContext` | PASS: no model changes since migration. Tool installed in temporary directory outside repository. |
| `dotnet-ef migrations script --idempotent` | PASS: generated `p004-platform-idempotent.sql` in the system temporary directory; test executed the generated idempotent script twice in a disposable PostgreSQL 17 database, then migrated down to `0`. |
| PostgreSQL catalog and negative tests | PASS: 9 tables, 19 exact constraint names, 18 indexes (10 PK/UNIQUE plus 8 explicit), 3 extensions, `f_unaccent('Đặng') = 'dang'`; audit identity CHECK, FK, status checks, UNIQUE and SERIAL behavior exercised. |
| `git diff --check` | PASS; no whitespace errors in tracked changes. Source specifications and P0-03 application contracts have no diff. |

The tests use disposable PostgreSQL containers and separate reader DbContexts for commit/rollback assertions. They cover pending saves, multiple saves, Result failure before/after save, exception, cancellation, persistence failure, and tracker reuse. No existing development or production database was touched. GitHub CI was not run.

## Post-G4 targeted corrections (2026-11-16)

- **P2-01:** A PostgreSQL integration test now writes UUID and TEXT audit identities, then queries each through a separate `AppDbContext` by schema-qualified `entity_type` plus the matching identity column. Other rows reuse the same UUID/TEXT key under different entity types, so `Assert.Single` also checks filtering. No audit interceptor was added; P0-05 still owns it.
- **P2-02:** `EfUnitOfWork` now records whether rollback has been attempted. A failed `Result` whose rollback fails throws that rollback error without another attempt. When an operation, `SaveChangesAsync` or `CommitAsync` throws, the original exception and stack trace remain primary; a secondary rollback/dispose failure is attached in `Exception.Data` under `TeaPremium.RollbackFailure` or `TeaPremium.TransactionDisposeFailure`. `OperationCanceledException` remains identifiable. A disposal or tracker-clear failure is not converted to success. Test-only EF transaction interceptors deterministically exercise normal and failed rollback, cancellation and failed commit without changing the production DbContext design.
- **P3-01:** `p0-04-readiness.md` now labels the SystemClock-only description as the pre-P0-04 baseline.
- **Verification:** `dotnet restore backend/TeaPremium.sln --locked-mode -v:q` PASS; `dotnet build backend/TeaPremium.sln --no-restore -v:q` PASS (0 warnings/errors); `dotnet test backend/TeaPremium.sln --no-build -v:q` PASS (11 unit, 6 architecture, **47 integration**); focused `PlatformDatabaseTests` PASS (31 cases); EF `has-pending-model-changes` PASS (none). Existing migration and model snapshot were not changed.

If PostgreSQL loses its connection during rollback or commit, the client may not know the final server outcome. The adapter throws rather than claiming a safe rollback or successful commit; operations staff must inspect database state and migration/transaction evidence before retrying a non-idempotent command. The interceptor tests simulate rollback/commit errors before server execution; they do **not** prove recovery from a physically broken connection. Targeted independent G4 review of these corrections remains required.

## P1 correction after targeted G4 (2026-11-17)

The targeted re-review found that a handler `DomainException` combined with rollback failure previously stayed a `DomainException`. The existing P0-03 `TransactionCommandDecorator` then converted it to an ordinary business `Result.Failure`, hiding the uncertain transaction outcome. `EfUnitOfWork` now emits an `InvalidOperationException` with a fixed, non-sensitive message only for this combination. Its `InnerException` is the original `DomainException`, with its stack trace; the rollback exception is retained under `Exception.Data["TeaPremium.RollbackFailure"]`. The existing decorator and API exception middleware remain unchanged: the former no longer treats this path as a business rejection, while the latter maps an unexpected exception to a generic `internal_error` response without logging exception details. A successful rollback still propagates the original `DomainException` for the existing business mapping.

Two new PostgreSQL 17 integration cases execute through the real `TransactionCommandDecorator`, `EfUnitOfWork` and a handler that saves then throws. They assert business failure and independent DbContext rollback when rollback succeeds; when a test-only transaction interceptor makes rollback fail, they assert a non-domain failure, inspect both exceptions and their stack traces, and check that rollback was attempted once. Database outcome is intentionally **not** asserted for a failed rollback. Final local checks: locked restore PASS; build PASS (0 warnings/errors); focused decorator tests 2/2 PASS; full suite 11 unit, 6 architecture and 49 integration PASS; EF pending-model-changes PASS (none); `git diff --check` PASS. The migration, model snapshot, `IUnitOfWork` interface and P0-03 decorators are unchanged. This correction requires another focused independent G4 review before owner-controlled PR preparation.

## Deployment and recovery

1. Review generated idempotent SQL and a migration bundle against the approved `schema.sql` before deployment. Run migration as `tp_owner` after a verified backup and before starting the new API, per `03-database.md:189–194`. Never call `Migrate` or `EnsureCreated` during API startup.
2. Before go-live, a disposable or resettable dev database may be recreated from migration after a failure. The test exercises `Down` to migration `0` only on a fresh disposable database. `Down` drops these new tables and `f_unaccent`; it is destructive if records exist, so do not run it on a populated database. Database extensions and the empty `platform` namespace may remain after downgrade and are harmless for recovery.
3. After real data exists, retain the backup, inspect failure state and migration history, and use a reviewed forward repair or restore procedure. Do not assume automatic rollback can preserve rows. Production migration execution and restore rehearsal remain deployment gates, outside this G3 task.

## Dependencies and G4 handoff

- P0-05: `[Audited]`, interceptor, atomic audit writes and nested PII/secret redaction. Append-only `tp_app` audit permissions need the P0-08 role setup; this slice does not create roles.
- P0-09: F-02 CSV provenance/import contract and administrative data initialization; provinces/wards are empty after migration. Settings API/defaults also remain there.
- Issue #5 (correlation ID in all business-handler logs) remains separately tracked; no concrete P0-04 dependency was found.
- G4 should independently compare migration SQL and live catalog with `schema.sql:36–135`, inspect all index predicates and CHECK expressions, repeat PostgreSQL negative/transaction tests, confirm migration downgrade only on disposable data, and check the diff for out-of-scope code or source-spec modifications.
