# Freshness Sweep Report

**Date:** 2026-08-05
**Mode:** diff
**Previous anchor:** `5a9bbe198` · **New anchor:** `upstream/main` `980896a1d`
**Worktree base:** `origin/main` `980896a1d`
**Dirty set:** 9 of 11 mechanical entries (7 prompt + 2 script) + 72 editorial docs + 0 unmarked docs
**Outcome:** 15 editorial docs drift-fixed · 8 mechanical regens updated (+1 verified clean) · 5 husks pruned (−4,539 lines, target 3,329 / cap 4,661) · 2 wheat migrations into `sections/Shifts.md` · 5 inbound-ref rewrites (3 of them in C# source) · 0 errors

`upstream/main` and `origin/main` were level at the same commit when this sweep started — normal right after a prod promotion.

Range themes (upstream `5a9bbe198..980896a1d`, 21 commits): a **controller-decomposition pass** that burned down all 15 remaining HUM0031-grandfathered controller methods (#1156) — the OAuth decision ladder left `AccountController` for the new `ExternalLoginService`, bulk-event CSV assembly left `EventsController` for `IEventService` + `EventOccurrenceExpander`, per-day signup toggling left `ShiftsController` for `ShiftSignupService.ToggleDayAsync`; **legal-document writer consolidation** (#1155 / nobodies-collective/Humans#751) deleting both `AdminLegalDocumentService` and the `LegalDocumentSaveChangesInterceptor`; **verified-email lookups retired from the repository** in favour of in-memory matching against the cached `UserInfo` set (#1152); **cross-section `EventSettings` consumers migrated to `BurnSettingsInfo`** (#1154 / nobodies-collective/Humans#809, Cantina + Agent); **AuditLog consuming Teams via `ITeamServiceRead`** instead of `Team` entities (#1157); **Store index auth-threading dropped** in favour of a view-checked policy (#1151 / nobodies-collective/Humans#815); the section-doc renames from the inventory freeze (`LegalAndConsent.md` → `Consent.md`, `Survey.md` → `Surveys.md`, #1159); and nine dependency bumps.

## Updated automatically (mechanical)

- `reforge-history.csv` — script: 125 rows, ok=1 fail=0
- `development-stats.md` — script: +1 daily row (table now 136 data rows); class/interface source reforge=1 day, regex-fallback=0
- `architecture/service-data-access-map.md` — **the big one this sweep.** The Legal area still carried a `### AdminLegalDocumentService (Scoped)` section, an "interceptor-driven cache flush" claim, and two Cache-Inventory rows citing `LegalDocumentSaveChangesInterceptor` — all describing code deleted in #751. Rewritten: `LegalDocumentSyncService` is sole writer, implements both `ILegalDocumentSyncService` and `IAdminLegalDocumentService`, and calls `ILegalDocumentCacheInvalidator.InvalidateAll()` directly. Also: `AuditViewerService` → `ITeamServiceRead`/`TeamInfo`; `CantinaRosterService` → `IBurnSettingsService`/`BurnSettingsInfo` (noting `AgentToolDispatcher` made the same migration in the same PR); new `ExternalLoginService` subsection under Users (no repository); the three deleted `IUserRepository` verified-email lookups replaced with the cached-`UserInfo` scan. Date → 2026-08-05
- `architecture/dependency-graph.md` — +1 node (`ExternalLoginService`, `:::users`) and +3 eager edges (→ `IUserService`, `IUserEmailService`, `IMagicLinkService`). Eager count 284 → 287, lazy stays 19, `linkStyle` range shifted to 287–305. **Verified independently:** the file now contains exactly 287 eager `-->` and 19 lazy `-. "lazy" .->` edges, so the rewritten `linkStyle` range is correct — an off-by-one there fails silently at render time. All other flagged services re-walked against their current constructors; `AdminLegalDocumentService` confirmed absent
- `authorization-inventory.md` — the 10-controller decomposition changed **zero** `[Authorize]` attributes; only call-site line numbers moved, corrected in §6 (`TeamController.cs` 166→160, 731→679; `ProfileController.cs` 18 `UserEmailOperations.Edit` sites, onsite-chip gate 1815→1872, privileged-approver check 1911→1968). `PolicyNames.TeamsAdminOrAdmin` re-verified as still registered-but-attached-to-no-controller (referenced only from `Team/Details.cshtml`)
- `controller-architecture-audit.md` — 10 controllers re-audited against current source; the decomposition moved bodies into services without renaming actions, so 0 actions added/removed. Date → 2026-08-05
- `About/Index.cshtml` — 6 production version bumps (Anthropic 12.35.1→12.39.0, EF Core / Localization / Authentication.Google 10.0.9→10.0.10, ClosedXML 0.105.0→0.105.1, Google.Apis.Drive.v3 1.75.0.4192→1.75.0.4218). **Independently cross-checked:** all 47 listed packages now match `Directory.Packages.props` exactly, with zero mismatches, and the test-only/analyzer bumps (Microsoft.NET.Test.Sdk, Meziantou.Analyzer, BannedApiAnalyzers) correctly absent from the page
- `docs/README.md` — full inventory re-verified (69 features, 35 sections excl. template, 25 guide + 6 common-questions rows); all links resolve; both renames already correct. One description fixed: the Legal & Consent row said "Consent Coordinator review **gate**" — it is an audit/review queue, not an access gate, per `Consent.md`'s own invariant
- Verified clean (dirty but no drift): `architecture/data-model.md` — the only in-range entity change was two new methods on `Event` (`ApplyIndividualEdit` / `ApplyBarrioEdit`), no new entity, field, or ownership change, so the generated index block was left byte-identical
- Not triggered: `guid-reservations`, `code-analysis-suppressions`

## Updated (editorial drift fixes — 15 docs)

**Legal/Consent (0 edits, verified):** `sections/Consent.md`, `features/legal-and-consent/legal-documents-consent.md` and `guide/LegalAndConsent.md` were all already correct — the section doc was updated inside #1155 itself and correctly describes `LegalDocumentSyncService` as sole writer with no interceptor; the feature and guide docs never named internal service classes. The stale interceptor prose lived in `service-data-access-map.md` instead, and was fixed there.

**Users/Auth (3):** `sections/Users.md` — four claims still attributed the OAuth callback ladder to `AccountController.ExternalLoginCallback` (Concepts, Invariants, Triggers, Architecture); all now credit `ExternalLoginService`. Its trigger line also named `IUserEmailService.LinkAsync`, **which does not exist on the interface** — corrected to `ReconcileOAuthIdentityAsync`, noting HUM0005 pins `ExternalLoginService` as its sole caller · `features/auth/magic-link-auth.md` — the Google-OAuth-linking flow diagram and the account-linking section both named `IUserEmailService.FindVerifiedEmailWithUserAsync`; the OAuth path actually calls `IMagicLinkService.FindUserByVerifiedEmailAsync` (`ExternalLoginService.cs:124,174`). The *magic-link* path genuinely does use `FindVerifiedEmailWithUserAsync` (`MagicLinkService.cs:29`), so only the OAuth occurrences were changed — the other two references were verified correct and left alone. The same section still told the reader to "modify `ExternalLoginCallback` in `AccountController`"; now points at `ExternalLoginService.CompleteExternalLoginAsync` · `sections/_Index.md` — added `ExternalLoginService` to the Users/Identity **Orchestrators** column (it injects no repository and coordinates `IUserService` / `IUserEmailService` / `IMagicLinkService`, which is this index's own definition of an orchestrator)

**Governance (3):** `features/governance/board-voting.md`, `features/governance/asociado-applications.md`, `features/onboarding/onboarding-pipeline.md` — all three claimed the Board voting queue is gated on Consent-Coordinator clearance (`ConsentCheckStatus = Cleared`). It is not: `ApplicationDecisionService.GetBoardVotingDashboardAsync` → `GetAllSubmittedWithVotesAsync`, whose query is `.Where(a => a.Status == ApplicationStatus.Submitted)` with no consent condition anywhere in the path. Verified at the repository, not just the service.

**Agent/Cantina (3):** `sections/Cantina.md` + `features/cantina/daily-roster.md` — the active-event read was described as `IShiftManagementService`; post-#809 it is `IBurnSettingsService` (`BurnSettingsInfo`), the whole point of the migration being that Cantina no longer touches the Shifts-owned `EventSettings` entity · `sections/Agent.md` — invariant 12's section-alias enumeration was missing the `LegalAndConsent` → `Consent` alias added by the rename. `sections/Cantina.md` also cited `CantinaAccessServiceTests.cs`, which does not exist — and contradicted the line directly above it, which says there is no `ICantinaAccessService`; corrected to the two roster test classes that do exist.

**AuditLog (1):** `sections/AuditLog.md` — team display-name lookup described as `ITeamService.GetByIdsWithParentsAsync` over `Team` entities; post-#1157 it is `ITeamServiceRead.GetTeamsAsync` filtered in-memory, which is what keeps this horizontal section from holding a vertical section's entities. Two adjacent display-name claims on the same sentences said `IProfileService.GetByUserIdsAsync`; the code uses `IUserServiceRead.GetUserInfosAsync`. The `freshness:auto` AuditAction catalog block was checked against the enum (185/185 values, no dupes, enum unchanged in range) and left untouched.

**Events (1):** `features/26-events.md` — the bulk-upload "Files to Change" section claimed no service-interface change was needed; `IEventService.BuildBulkUploadTemplateAsync` now owns the template CSV/banner assembly. The same bullet credited `ParseCsvRows` and `ValidateBulkRows` to the controller: **`ParseCsvRows` exists nowhere in the codebase** (parsing is `BulkEventCsvParser.Parse`, called from `EventsController.cs:790`) and `ValidateBulkRows` is a private static on `EventService` (`:503`). Corrected.

**Shifts/Profiles (2):** `features/profiles/dietary-medical-nudge.md` — US-35.6 described `ShiftsController.SignUp` / `SignUpRange` redirecting to the dietary form. Neither action exists (removed in #876); the gate is now `ToggleDay`, redirecting when `ShiftSignupService.ToggleDayAsync` returns `NeedsDietaryFirst`, with `returnAction=signup` only · `features/email/email-outbox.md` — `CleanupEmailOutboxJob` documented as "runs daily" deleting "sent/failed" messages. Its Hangfire cron is `0 3 * * 0` (weekly, Sunday 03:00 UTC) and `DeleteSentOlderThanAsync` filters `Status == Sent` only, so `Failed` rows are retained. `sections/Email.md` already had the schedule right.

**Architecture (1):** `architecture/design-rules.md` — added `ExternalLoginService` to the §8 Users/Identity row, and added a missing **Cantina** row to the §8 Table Ownership Map. Cantina owns no tables, but §8 already carries table-less sections (Onboarding, Mailer, Scanner), and the new `memory/architecture/sections-are-logical-units.md` atom — which landed inside this sweep's range — names Cantina explicitly as the case for "owning tables is not a requirement". The row's three read interfaces were taken from `CantinaRosterService`'s constructor, not from the flag that reported the gap. `coding-rules.md`, `conventions.md`, `code-review-rules.md` and `roslyn-analysis.md` were checked against the analyzer, the two changed architecture tests, and the two new memory atoms: no contradictions.

**Verified no-drift:** the remaining 57 triggered docs. Notably the whole Teams cluster (7 docs), the whole Store cluster (3), and 6 of the 7 Shifts docs — #1151/#1156 are internal decompositions with no externally observable change, and the docs describe behaviour rather than method bodies. `features/global/gdpr-export.md`, `guide/YourData.md` and `features/user-search-overhaul.md` re-checked against their changed services and confirmed untouched by them.

## Pruned — 5 husks, −4,539 lines (target 3,329 = 5%; cap 4,661 = 7%)

Doc corpus 66,587 → 62,048 lines. Two batches, both fully mined before deletion.

### Wheat migrated (2 blocks, both code-verified)

| Source §section → Destination | Verified by |
|---|---|
| volunteer-tracking-export-design §Team Palette → `sections/Shifts.md` (Invariants): export colours are derived, never stored — `SHA256(teamId.ToString("D"))` into a fixed 20-entry palette, no `Team.HexColor` column; the `"D"` format is load-bearing; changing palette length **or order** re-maps every team, so exports either side of such a change are not colour-comparable; collisions accepted | `TeamPalette.cs:18-25` — SHA256 of the `"D"` string, `index % Palette.Length`, 20 entries; no `HexColor` anywhere on `Team` |
| volunteer-tracking-export-design §Out of Scope → `sections/Shifts.md` (Triggers): the XLSX export deliberately writes **no** audit entry — the grid carries burner names only; if it is ever widened to PII, an audit entry lands in the same change | `VolunteerTrackingController` — `IAuditLogService` is injected and used by every mutating action (`:228,239,263,300,332,364`) but not by `ExportXlsx` (`:112`) |

**One spec claim corrected rather than migrated.** The design doc asserted that "adding/reordering palette entries without changing length leaves mappings stable". Reordering moves which hex sits at a given index, so a team's colour changes even at constant length — `TeamPalette.cs:9`'s own comment says so ("Order matters only for determinism — changing it shifts every team's color"). The migrated invariant states length **or** order.

### Husks deleted

| Batch | Files | Lines |
|---|---|---|
| Volunteer tracking export | `plans/2026-05-23-volunteer-tracking-export.md` (2,761), `specs/2026-05-23-volunteer-tracking-export-design.md` (289) | 3,050 |
| Dietary / medical to Profile | `plans/2026-05-23-dietary-medical-nudge-impl.md` (1,238), `plans/2026-05-25-dietary-medical-to-profile.md` (149), `specs/2026-05-25-dietary-medical-to-profile-design.md` (102) | 1,489 |

All-chaff drops with nothing left to migrate: the volunteer-tracking **plan** is a pure chunk/task/TDD-checkbox list whose only rationale content is a "spec deviations" table about project conventions; the dietary **nudge-impl plan** describes a superseded design (its premise — "save to existing `VolunteerEventProfile` columns, no migration" — was reversed by the later move to `Profile`, verified against `Profile.cs` and `VolunteerEventProfile.cs`, where the old columns survive only as XML-doc'd tombstones); the two dietary to-Profile docs are a stage/task checklist plus a design spec whose every verified-true statement is already an invariant in `sections/Profiles.md`, `sections/Shifts.md` and `sections/Cantina.md` (checked line by line: `Profile.cs` has the six fields, `IShiftManagementService.GetShiftProfileAsync` has no `includeMedical` parameter, and `CantinaRosterService` reads dietary via `IUserServiceRead` and never `MedicalConditions`).

### Inbound refs rewritten (5)

- `src/Humans.Application/Services/Shifts/TeamPalette.cs:20` — "locked by spec — see 2026-05-23-volunteer-tracking-export-design.md" → points at `docs/sections/Shifts.md` (Invariants), where the rule now lives
- `src/Humans.Domain/Entities/Profile.cs:63` and `src/Humans.Domain/Entities/VolunteerEventProfile.cs:8` — XML-doc/comment citations of the deleted dietary design spec → `docs/sections/Profiles.md`
- `docs/features/profiles/dietary-medical-nudge.md:146,172` — two citations of the same deleted spec → `sections/Profiles.md` / `sections/Shifts.md`

The inbound-ref scan covered `docs/`, `memory/`, `src/`, `tests/`, `scripts/` and `.claude/` — three of the five hits were in C# source, which a docs-only scan would have missed (the failure mode caught in last sweep's review).

### Not eligible / not analysed this sweep

- `architecture/tech-debt-2026-04-23.md` — still carries genuinely-open items
- `specs/2026-04-25-freshness-sweep-design.md` — the active spec for this skill
- Budget candidates left for the next sweep (analysis not started — a budget decision, not a punt): `plans/2026-05-25-holded-finance-feature2-creditor.md` (1,642), `plans/2026-05-25-dietary-prompt-tightening.md` (1,562), `plans/2026-06-10-table-component.md` (1,482), `plans/2026-06-09-ical-feed.md` (1,410), `plans/2026-05-25-holded-finance-feature1-actuals.md` (988)

## Flagged for human review

- `docs/plans/2026-08-03-section-dependency-dag.md:251,336` — references `AdminLegalDocumentService` and `LegalDocumentSaveChangesInterceptor.cs:75` as live code; both were deleted in #1155, which landed after that plan was written. Left as-is: it is a dated `@5a9bbe198` planning snapshot, and `docs/plans/**` is explicitly outside the editorial trees. Noted so the G0 follow-up work does not act on the stale edges.
- `features/governance/membership-tiers.md:189` — **resolved (Peter, 2026-08-05): the Consent Coordinator check *was* a gate, but is not any more.** The rule now reads "a legal name plus the required consents is sufficient", with the historical gate noted as an audit annotation only.

## Questions

Delivered to Peter inline at the end of this sweep (the report is the record, not the delivery channel). All three raised; two answered, one still open.

1. **`membership-tiers.md` Business Rule 2** — "Volunteer doesn't require an Application — **consent check clearance** is sufficient" contradicted `sections/Onboarding.md` and `MembershipCalculator` (which never reads `ConsentCheckStatus`). **Resolved (Peter, 2026-08-05): the CC check was a gate historically, but is not any more.** The doc was corrected to "a legal name plus the required consents is sufficient", recording the CC check as an audit annotation. Both readings were live at different times, which is why the sweep asked instead of guessing.

2. **Section label vs section-doc filename** — the inventory freeze renamed the section to **Consent**, but the H1, the `_Index.md` row, and the `docs/README.md` row still read "Legal & Consent" (#1159 deliberately moved files only). **Resolved (Peter, 2026-08-05): rename the label too — "Legal" is wanted for something else later.** Applied across the living docs: `sections/Consent.md` H1, `sections/_Index.md` (row + Admin note), `docs/README.md` (2 rows), `architecture/design-rules.md` (§8 row + 2 prose mentions), `architecture/data-model.md` (8 mentions), and the cross-section references in `sections/{Onboarding,Governance,Profiles,Users}.md`. **Deliberately NOT renamed:** `docs/guide/LegalAndConsent.md` and the 8 guide docs linking to it — that is an end-user page whose *filename* is served by `GuideController` and aliased in `AgentSectionKeys` (`LegalAndConsent`→`Consent`), so renaming it is a code-touching change, not a label change. Flagged as follow-up.

3. **Homeless wheat from the dietary spec** — "`MedicalConditions` stays a plain `ProfileInfo` string, made hard to leak by a focused review/test that every serializer either omits it or gates it behind the policy; a wrapper type forcing a policy token to read medical was **rejected as over-engineering at this scale**, but noted as the fallback if leaks surface." Verified still true (`Profile.cs:92`, no wrapper type in the domain). **Resolved (Peter, 2026-08-05): keep it.** Migrated as a new `design-rules.md` §8c ("Special-category (GDPR Art. 9) fields are guarded by convention, not by type") — it records that `MedicalConditions` is a plain `string?` riding on the cached `UserInfo`, names the three load-bearing parts of the convention that contain it (DTO omission by construction, a per-surface omission test such as `CantinaRosterServiceTests.GetWeeklyRoster_MedicalConditionsNeverInDto`, and the documented caller obligation plus negative access rules), and records the wrapper type as the designated escalation if a leak ever occurs. This is the one place the sweep created new prose in a regulation doc rather than correcting existing prose.

## Proposed for review

None — all prune candidates were resolved this sweep (migrate / drop / verify-clean). No uncertain wheat was queued for a future pass; the one uncertain item is Question 3 above, pending Peter's answer.

## CI

`MailerLiteClientRetryTests.AssignSubscriberToGroupAsync_ClampsAbsurdRetryAfter_ToCeiling` failed on one run of this branch and passed on another with only markdown between them (`31019573457` green → `31020027058` red); `main` was green throughout and this PR touches no Mailer code. Cause: the test arms `cts.CancelAfter(250ms)` up front, but three handler round-trips must complete before `MailerLiteClient` logs the clamped-delay warning the assertion looks for — on a loaded runner the token cancels first and `logger.Entries` is empty. Filed as nobodies-collective/Humans#982 (`bug`, `section:infra`, `size:S`) with a deterministic fix suggested (cancel from the handler when it serves the 429, rather than on a timer). **Not fixed here** — an unrelated flaky test does not belong in a docs sweep, and raising the timeout would be the surgical fix the hard rules forbid.

## Skipped (errors)

None — but **four subagents went idle without delivering their result payload** (`drift-admin-gdpr`, `mech-about-packages`, and *both* attempts at the volunteer-tracking-export prune batch). This is the same failure mode recorded last sweep. Recovery: `drift-admin-gdpr` re-sent on request; `mech-about-packages` had already written its edit, which was read back from the worktree diff and independently verified against `Directory.Packages.props`; the volunteer-export prune was re-dispatched once, failed again, and was then **performed by the orchestrator directly** rather than dropped — which is why that batch's wheat is code-verified above. No entry was skipped.
