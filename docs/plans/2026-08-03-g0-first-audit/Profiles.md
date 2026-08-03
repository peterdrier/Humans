# Profiles — G0 First Audit

**Section:** Profiles · **Kind:** shared contract (Foundational, per `CONTEXT.md`) · **Audited:** 2026-08-03 @ 5a9bbe198

> Scored jointly with [Users.md](Users.md) per `memory/architecture/users-profiles-one-section.md` — Users, Profiles, and UserEmail are **one ownership section ("Humans")**. This file covers the Profile/ContactField/UserEmail/CommunicationPreference sub-aggregates; see Users.md for the User/EventParticipation/AccountMerge sub-aggregates. Findings that apply to the combined section are duplicated in both files so each stands alone.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Humans --tables profiles,contact_fields,user_emails,communication_preferences,volunteer_history_entries,profile_languages,users,event_participations,account_merge_requests` → **0 violations**. |
| 2 | One writer-service per table (no interceptor workarounds) | **FAIL** | `UserInfoSaveChangesInterceptor` (EF `SaveChangesInterceptor`, registered on both scoped and factory contexts per `docs/sections/Users.md` Architecture section) catches Identity-machinery writes that bypass the service surface (`UserManager.UpdateAsync`, OAuth `UserEmail` row creation) and routes them through `IUserInfoInvalidator`. This is a documented, explicit interceptor workaround on `users`/`user_emails`-adjacent writes. Per the parent orchestrator's briefing this is **known, explicitly out-of-scope for tonight's lanes** — recorded here honestly rather than re-litigated. |
| 3 | No EF entity leaks across boundary | **FAIL** | `ApplicationServiceEntityReadReturns.baseline.txt` carries 2 rows for this cluster: `IAccountProvisioningService.FindOrCreateUserByEmailAsync → User` and `IUserService.GetByIdsAsync → User`. Both are pre-existing, ratchet-baselined debt (not new). Also: `AccountMergeRequest.TargetUser` / `SourceUser` / `ResolvedByUser` navigation properties are **live, un-stripped, and not even `[Obsolete]`-marked** cross-domain EF navs (`docs/sections/Profiles.md` §AccountMergeRequest: "predate the §15i nav-strip work; the merge admin views read them directly today"). This is worse than the Teams-style `[Obsolete]`-marked debt — it's undocumented-as-debt-in-code, only flagged in prose. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No dedicated baseline file for `CrossSectionEfJoinAnalyzer` exists (build-time enforced, no ratchet mechanism) — no entries for this section found anywhere in `tests/Humans.Application.Tests/Architecture/Baselines/`. |
| 5 | No `[Grandfathered]` / no owned baseline rows (or queued G2 item) | **FAIL** | The 2 `ApplicationServiceEntityReadReturns` rows above are owned by this cluster and have no queued G2 demolition item that I could find. |
| 6 | Controllers thin (no HUM0031 grandfathers) | **FAIL (tracked)** | `ProfileController.cs` carries **3** `[Grandfathered(ruleId: "HUM0031", …)]` markers (lines ~207, ~1588, ~2113) — one explicitly justified (cross-section post-save orchestration must stay controller-side per `no-leaf-to-director-callbacks`), two flagged as raw "worst-offender at HUM0031 introduction" (36/46 statements, cc 21/27). All three carry `issueRef: "nobodies-collective/Humans#857"` — the in-flight burndown lane (Lane 2 of this same orchestration run). |
| 7 | `docs/sections/Profiles.md` current | PASS (high confidence) | Extremely detailed, references very recent work (issue #758 email-delete guards, #703 caching decorator, #635 §15i nav-strip, #690 popover fallback). No drift observed against the code read this pass. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests on real Postgres, zero EF-InMemory | **FAIL** | `tests/Humans.Application.Tests/Repositories/ProfileRepositoryTests.cs:22` uses `.UseInMemoryDatabase(Guid.NewGuid().ToString())`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **PARTIAL/FAIL** | `ProfileServiceTests.cs`, `ContactFieldServiceTests.cs`, `CommunicationPreferenceServiceTests.cs` all inherit `ServiceTestHarness` (base class itself is `UseInMemoryDatabase`-backed, `tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs:54,71`) rather than mocking the repository. `UserEmailServiceTests.cs` shows **zero** `ServiceTestHarness` hits — clean, properly mocked. Mixed within the section. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively verified. Spot-checked email-delete-guard and primary-invariant behaviors are documented in the section doc as service-enforced with named recovery paths (`ClearPrimaryAsync`, `EnsurePrimaryInvariantAsync`) but per-invariant test traceability wasn't confirmed this pass. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No `Skip="..."` hits in this section's test files. |
| 5 | Tests grouped under section | PARTIAL | `ProfileServiceTests.cs`, `ContactFieldServiceTests.cs`, `CommunicationPreferenceServiceTests.cs`, `UserEmailServiceTests.cs` sit flat under `tests/Humans.Application.Tests/Services/` rather than a `Services/Profiles/` subfolder (a `Profiles/` and `Profile/` subfolder do exist per the directory listing but are thin — most tests are flat-named at the Services root). This is a repo-wide pattern, not unique to Profiles, but it means G5's "movable with it" bar isn't met yet. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `UserInfoSaveChangesInterceptor` bypasses the one-writer-service rule for Identity-machinery writes | `Humans.Infrastructure` interceptor registration (both scoped + factory contexts) | Out of scope tonight per orchestrator briefing — retire once `IdentityFindByEmailRestrictionsTests`-style routing covers all Identity write paths through `IUserService`. Track as a named G2/G1 follow-up issue if not already filed. | y (service-layer refactor) |
| `IUserService.GetByIdsAsync` / `IAccountProvisioningService.FindOrCreateUserByEmailAsync` return `Humans.Domain.Entities.User` | `ApplicationServiceEntityReadReturns.baseline.txt` rows 28–29 | Convert both to return `UserInfo`/DTO projections and remove the baseline rows — likely a Users-section change since both interfaces live under `Interfaces.Users`. | y |
| `AccountMergeRequest.TargetUser`/`SourceUser`/`ResolvedByUser` cross-domain navs live and un-stripped | `Humans.Domain.Entities.AccountMergeRequest` + `AccountMergeService`/merge admin views | Strip navs, route display data through `IUserService.GetByIdsAsync` (once that itself stops returning entities) per the doc's own "Touch-and-clean guidance." | y |
| `ProfileController` carries 3 HUM0031 grandfathers | `src/Humans.Web/Controllers/ProfileController.cs` | Tracked under nobodies-collective/Humans#857 (in-flight, Lane 2 tonight). No new action needed from this audit. | y |

## G2 queue notes

Profile picture dual-write (`ProfilePictureData` DB fallback), `Profile.IsSuspended` obsolete column, `User.GoogleEmailStatus`/`GoogleEmail` shadow columns, `UserEmail.IsOAuth`/`DisplayOrder` shadow columns are all named demolition-inventory candidates already flagged in the section doc as "pending a deferred drop migration" — these feed G2 directly once soak windows close. No new items surfaced this pass beyond what the doc already tracks.
