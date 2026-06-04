# Freshness Sweep Report

**Run:** 2026-06-03T11:34:28Z
**Mode:** diff (batch)
**Previous anchor:** `e2b9630e`
**New anchor:** `37dc784f` (upstream/main HEAD)
**Worktree base:** `origin/main` @ `37dc784f` (origin/main == upstream/main this cycle — clean post-promotion state, no base reconciliation needed)

## Summary

189 files changed in `e2b9630e..37dc784f`. Dominant themes:

- **Team Early Entry** (#860) — Teams now implement `IEarlyEntryProvider`: new `TeamEarlyEntryGrant` entity + table (`20260602125133_AddTeamEarlyEntry`), `Team.EarlyEntryEnabled` gate, per-team EE management page on `TeamAdminController`, new `EETeamAdmin` cross-team role + `ManageEarlyEntry` resource operation, three new audit actions, and a `TeamEarlyEntry` GDPR export slice.
- **Camps / Barrios** — EE allocation badge on the Members page (#858), opt-in "Show lead positions" directory coverage pills (#821), barrio-lead fallback in role-email Google Groups (#859), Barrio Guide 2026 link (#861).
- **Cross-section read/write splits & dead-surface trims** (#847 Campaign, #850 Teams, #851 Governance, #852 Notifications, #854 Consent, #848 Issues, #849 Agent) — verified to introduce **no** doc-level contradictions: the full service interfaces inherit their new `*Read` interfaces, so existing doc citations of inherited read methods remain true; the only truly-deleted symbol cited by a doc was the consent repo rename (fixed below).

Counts: **7 mechanical entries updated**, 2 mechanical verified no-op, 1 not-dirty, 1 skipped (cloc). **12 editorial docs drift-fixed**. **3 husks pruned (−6,161 lines, 5.6%)**.

## Updated automatically (mechanical)

- `reforge-history` — appended 3 day-rows (2026-05-31, 06-01, 06-03) via `generate-reforge-history.sh`.
- `about-page-packages` — bumped Anthropic 12.23.0→12.24.1, MailKit 4.16.0→4.17.0, Google.Apis.CloudIdentity.v1 1.74.0.4150→1.74.0.4161 on the About page; no cards added/removed.
- `authorization-inventory` — added `EETeamAdmin` role + `ManageEarlyEntry` resource op (TeamAuthorizationHandler / TeamAdminController EarlyEntry actions); re-gated the logo build-hash tooltip to FullAdmin (AdminOnly) per 3c6a878e; corrected a pre-existing ShiftAdminController resolver inaccuracy.
- `controller-architecture-audit` — added the four `TeamAdminController` EarlyEntry actions (all OK per conventions); date → 2026-06-03.
- `dependency-graph` — verified all 80 service ctors; edge set already accurate, annotated Team/Camps/Shifts EE providers and the campaign/governance read-split interfaces in the read-split list.
- `service-data-access-map` — added Teams `team_early_entry_grants` table + `ITeamRepository` EE methods + `IEarlyEntryProvider`/`IEarlyEntryInvalidator`; refreshed governance (#851) and consent (#854) read-split consumers.
- `data-model-index` — added `TeamEarlyEntryGrant` (Teams) to the entity index inside the `freshness:auto` block.

## Updated automatically (editorial drift-fix)

Team Early Entry feature + footprint:

- `docs/sections/Teams.md` — EE routes, `EETeamAdmin` actor + `ManageEarlyEntry` auth, EE invariants (enabled-gate, toggle-keeps-grants, idempotent remove), audit/erasure triggers, `IEarlyEntryProvider` cross-section dependency, `TeamEarlyEntryGrant.cs` trigger.
- `docs/features/teams/teams.md` — added user story for managing Team Early Entry.
- `docs/guide/Teams.md` — user-facing Early Entry blurb + `EarlyEntryEnabled` admin setting.
- `docs/sections/AuditLog.md` — three `EarlyEntry*` audit actions.
- `docs/features/global/gdpr-export.md` — `TeamEarlyEntry` export slice.
- `docs/sections/Auth.md` — `EETeamAdmin` role + `ManageEarlyEntry` team operation.
- `docs/features/global/administration.md` — `EETeamAdmin` assignable role.
- `docs/guide/Governance.md` — `EETeamAdmin` added to the manageable-roles list.

Camps:

- `docs/sections/Camps.md` — barrio-lead Google-Group fallback (#859) on `GetExpectedAsync`; "Show lead positions" directory coverage as counts-only (no identity leak, strengthens negative-access rule).
- `docs/features/camps/camps.md` — lead-position coverage view, EE allocation badge, barrio-lead email fallback.
- `docs/guide/Camps.md` — Barrio Guide 2026 link, "Show lead positions" toggle, EE slots badge.

Cross-section refactor drift (in-scope, concrete):

- `docs/sections/LegalAndConsent.md` — #854 consent dedup renamed the single-user repo reads to `...ForUserIds...`; replaced the dead `ConsentRepository.GetAllForUserAsync` citation with the surviving `GetAllForUserIdsAsync` and refreshed line numbers (×2).

## Pruned (−6,161 lines, 5.6% of 109,375)

Shipped-feature implementation-plan husks (>30 days old; companion `-design.md` design specs all survive as the rationale archive):

- `docs/superpowers/plans/2026-03-15-ticket-vendor-integration.md` (3,258 L) — all chaff: 27 task lists + code samples now in `src/`; design rationale survives in the companion spec and is documented far more richly in `docs/sections/Tickets.md` + `docs/features/tickets/ticket-vendor-integration.md`.
- `docs/superpowers/plans/2026-04-20-pr235-cache-collapse.md` (1,702 L) — all chaff: caching-decorator wheat fully captured (and superseded) by `design-rules.md` §15 and the surviving design spec.
- `docs/superpowers/plans/2026-04-20-user-guide.md` (1,201 L) — all chaff: guide content shipped to `docs/guide/` (now 28 files); editorial rules survive in the companion design spec.

**Wheat migrated:** None — all three husks' durable signal was already captured in surviving design specs, `design-rules.md` §15, or the shipped living docs (verified against current source).

**Inbound refs retargeted:** None within living docs. (`maintenance-log.md:26` cites the deleted user-guide plan — see flag below; it is hand-maintained, so left untouched.)

## Proposed for review

None — all prune candidates resolved this sweep (0 migrations, 3 deletions).

## Flagged for human review

- **`docs/architecture/maintenance-log.md:26`** — the "User guide created" row cites the now-deleted `docs/superpowers/plans/2026-04-20-user-guide.md`. The sweep does not edit the hand-maintained maintenance log; please retarget that `Plan:` citation to the surviving spec `docs/superpowers/specs/2026-04-20-user-guide-design.md` by hand.
- **`docs/guide/Governance.md` BoardManageableRoles list** — pre-existing drift: the enumerated list omits `CantinaAdmin` (present in `RoleNames.BoardManageableRoles`). This sweep added only `EETeamAdmin` (in-scope for #860). A future Auth/Governance pass should reconcile the full list against `RoleNames.BoardManageableRoles`.
- **Pre-existing `ITeamService.GetTeamNamesByIdsAsync` citations** — `docs/sections/{Calendar,Shifts,Feedback,AuditLog,LegalAndConsent}.md` cite `ITeamService.GetTeamNamesByIdsAsync`, which is absent from `src/` (and was already absent at the prior anchor `e2b9630e`). Out of this sweep's changed-file scope; needs a dedicated pass to identify the replacement (likely `GetTeamsAsync`/`TeamInfo` stitching) and update those docs.
- **`docs/architecture/data-model.md:157`** (ownership map, outside the `freshness:auto` block) — cites `GetUserConsentsAsync`, absent from `src/` at both anchors. Pre-existing, hand-maintained table row; flag only.

### Unmarked editorial docs (no `freshness:triggers` — add scoped triggers in a future pass)

These editorial docs carry no `freshness:triggers` marker, so they cannot be diff-scoped and were not reviewed this sweep:

- `docs/features/26-events.md`, `docs/features/27-guide-browser.md`, `docs/features/43-google-group-membership-sync.md`, `docs/features/test-system-reliability.md`
- `docs/features/agent/agent-section.md`, `docs/features/scanner/scanner-barcode.md`
- `docs/sections/Agent.md`, `docs/sections/Debug.md`, `docs/sections/Events.md`, `docs/sections/Mailer.md`, `docs/sections/Scanner.md`, `docs/sections/_Index.md`, `docs/sections/admin-shell.md`
- `docs/guide/AiHelper.md`, `docs/guide/EmailAccount.md`, `docs/guide/SigningIn.md`, `docs/guide/TicketTransfers.md`, `docs/guide/TwoStepVerification.md`, `docs/guide/YourData.md`

## Questions

None posed to Peter this sweep. (One tooling request: `cloc` is not installed on this machine — see Skipped.)

## Skipped

- `dev-stats` — **skipped (tooling):** `generate-stats.sh` requires `cloc`, which is not installed on this machine. Per project rule, no workaround was applied; install `cloc` (`sudo apt-get install -y cloc`) and a future sweep will backfill the 2026-06-03 row (the script is incremental).
- `code-analysis-suppressions` — not dirty (no change to `Directory.Build.props` / `BannedSymbols.txt`).
- `docs-readme-index` — verified no-op: the index tables already match disk (features 63/63, sections 34/34, guide 25/25; no adds/removals).
- `guid-reservations` — verified no-op: all deterministic-GUID blocks accurate; `TeamEarlyEntryGrantConfiguration` introduced no GUID literal / HasData seed.
