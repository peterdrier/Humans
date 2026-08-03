# Issues — G0 First Audit

**Section:** Issues · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner Issues --tables issues,issue_comments` → 0 violations. |
| 2 | One writer-service per table | PASS | `IIssuesRepository` (impl `Humans.Infrastructure/Repositories/Issues/IssuesRepository.cs`) is the only `DbContext` consumer for both tables; `IssuesService` the sole write orchestrator. |
| 3 | No EF entity leaks across boundary | PASS | `.Include(i => i.Reporter/.Assignee/.ResolvedByUser)` and `.Include(c => c.SenderUser)` are explicitly never called (doc's own touch-and-clean guidance forbids reintroducing them); display data stitched via `IUserService.GetByIdsAsync` in `IssuesService.StitchCrossDomainNavsAsync`. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL — corrected 2026-08-03** | No Issues rows in any baseline file, but that can't establish a pass: HUM0024 is **attribute**-allowlisted, not baseline-file-based. `IssueConfiguration.cs` and `IssueCommentConfiguration.cs` both carry active `[Grandfathered("HUM0024", …)]` markers over the four cross-section User relationships named in predicate 5 (`ReporterUserId`, `AssigneeUserId`, `ResolvedByUserId`, `SenderUserId` → Users). |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PARTIAL | 4 cross-section navs are kept `[Obsolete]`-marked for FK/cascade wiring: `Issue.Reporter`, `.Assignee`, `.ResolvedByUser`, `IssueComment.SenderUser` (wrapped in `#pragma warning disable CS0618` in EF configs). This is the documented, deliberate design-rules §6c pattern (nav kept only for FK wiring, never walked) — lower urgency than a live/unmarked nav, but is literally an `[Obsolete]` cross-section nav per the G1 wording, so recording as a queued G2 item: convert to typed-FK form (`HasOne<User>().WithMany().HasForeignKey(...)`) the way GoogleIntegration already did for `SyncServiceSettings.UpdatedByUser`, dropping the nav property entirely. No `[Grandfathered]` hits; no other baseline rows. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | No `Grandfathered` hits on `IssuesController.cs` / `IssuesApiController.cs`. |
| 7 | `docs/sections/Issues.md` current | PASS | Detailed and matches code, including the derived (non-stored) ball-in-court logic and the audit-log-reconstructed activity thread. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | **PARTIAL — no repository test exists at all** | Grep for `class.*IssuesRepository|new IssuesRepository\(` across `tests/`: only `Services/IssuesServiceTests.cs` matches (service-level, uses a mock repository interface, not the real repo). There is no `tests/.../IssuesRepositoryTests.cs` or equivalent — `IssuesRepository` itself has zero dedicated repository-layer test coverage. Trivially "zero EF-InMemory" but only because there are no repo tests to have used it. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | Another inherited-setup false negative: `IssuesServiceTests` extends `ServiceTestHarness` and constructs a concrete `IssuesRepository(DbFactory)`, exercising it against the harness's `UseInMemoryDatabase`-backed `HumansDbContext` (`ServiceTestHarness.cs:54,71`). The absence of a literal `HumansDbContext` reference in the test file proves nothing. Add to the repository-mocking conversion queue (#766). |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped. Auto-reopen-on-reporter-comment, comment-and-mark-resolved atomicity, and the 6-month retention sweep are documented invariants; plausibly covered by `IssuesServiceTests.cs` and a retention job test, but no line-level confirmation done this pass. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | **PARTIAL** | `IssuesServiceTests.cs` lives in the shared `tests/.../Services/` folder, `IssuesArchitectureTests.cs` correctly in `Architecture/`, `IssuesApiControllerTests.cs` in `Controllers/`, `IssuesAuthorizationHandlerTests.cs` in `Authorization/` — none grouped under a dedicated `tests/.../Issues/` folder the way GoogleIntegration/Notifications/Camps are. Not movable-with-section as-is. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| 4 `[Obsolete]`-marked cross-section navs kept for FK wiring | `src/Humans.Domain/Entities/Issue.cs`, `IssueComment.cs` | Convert `ReporterUserId`/`AssigneeUserId`/`ResolvedByUserId`/`SenderUserId` to typed-FK form (drop the nav properties entirely), matching the GoogleIntegration pattern. | y |
| **Added 2026-08-03:** 2 HUM0024 configuration grandfathers | `IssueConfiguration.cs`, `IssueCommentConfiguration.cs` | Same four relationships as the row above, seen from the EF-configuration side (predicate 4 was scored off baseline-file greps, which can't see attribute allowlisting). The typed-FK conversion above retires the navs; retiring the `[Grandfathered]` markers and cutting the physical FKs is the G2 half. | y (nav/attribute work); FK cut is G2 |

## G2 queue notes

**Corrected 2026-08-03.** The typed-FK conversion above is schema-neutral, but that is *not* the same as having no G2 work: `IssueConfiguration` and `IssueCommentConfiguration` define **four physical User FK constraints** (`ReporterUserId`, `AssigneeUserId`, `ResolvedByUserId`, `SenderUserId` → `AspNetUsers`), and dropping the nav properties or the HUM0024 attributes does not drop those constraints. All four FK cuts belong in Issues' G2 queue; otherwise the section could enter schema cleanup with only the configuration refactor scheduled and keep every cross-section database dependency.

## Headline

IssuesRepository has zero dedicated test coverage.
