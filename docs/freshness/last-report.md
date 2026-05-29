# Freshness Sweep — 2026-05-29

| | |
|---|---|
| Previous anchor | `cdd850bd` |
| New anchor | `cd9a9345` |
| Mode | diff |
| Mechanical entries dirty | 8 of 11 |
| Mechanical files changed | 5 |
| Editorial docs triggered & reviewed | 36 |
| Editorial docs corrected for drift | 11 |
| Editorial unmarked (no coverage) | 18 |
| Husks pruned | 11 files (−7,065 lines) |
| Wheat migrated | 0 (cluster pre-extracted by prior sweeps; re-verified) |
| Docs tree | 121,855 → 114,790 lines (−5.80%) |

## Updated automatically

- **dev-stats** — appended today's row (`docs/development-stats.md`); reforge data lacked a 2026-05-29 row so class/interface counts for today came from the regex fallback (script's intended behavior)
- **authorization-inventory** — re-scanned all controllers/handlers/`AuthorizeAsync` sites; corrected `DevSeedController.ResetDashboard` (now `PolicyNames.AdminOnly`, not `[Authorize(Roles)]`), updated the §7 note to reflect zero `[Authorize(Roles)]` attributes remain, fixed §6 line drifts (`HumansCampControllerBase` 55/85→58/88, `CityPlanningApiController` 276/301/339→274/299/337), bumped refresh date
- **service-data-access-map** — refreshed Camps (CampService folds in the EarlyEntry projection + adds `IEarlyEntryInvalidator`/`ICampRoleCampAccess`; `CampRoleService` now uses narrow `ICampRoleCampAccess` + `IUserServiceRead`/`IUserEmailService`/`ICampInfoInvalidator` + `IGoogleGroupMembershipSource`) and corrected CityPlanning/Store/NotificationMeterProvider/OnsiteRoster to the read-split `ICampServiceRead`/`ITeamServiceRead`/`IUserServiceRead` surfaces
- **dependency-graph** — verified all service ctors against the Mermaid graph (edges/nodes/lazy edges all current, no diagram body changes); fixed stale prose that claimed `CampRoleService` injects `ICampService` (it now uses the narrow `ICampRoleCampAccess` port)
- **controller-architecture-audit** — verified all 9 trigger-changed controllers (CampController, CampApiController, AccountController, CityPlanningController, CityPlanningApiController, EventsController, HumansCampControllerBase, MailerAdminController, ProfileBackfillAdminController) against live source — every action/route/verb still matches; no additions/removals/renames; header date bumped to 2026-05-29

### Already current — no change needed

- **docs-readme-index** — 33 sections / 63 features / 25 guide docs all present with matching descriptions; the 5 changed section docs (Budget, Camps, CityPlanning, Mailer, Tickets) already had accurate index rows
- **data-model-index** — entity-index table verified against `src/Humans.Domain/Entities/`; the `CampSeason.cs` change (the trigger) is already covered by the existing Camps row; no rows to add/remove/relink
- **reforge-history** — script reported "No new days to snapshot" (last snapshot 2026-05-28); no row appended

### Skipped (not dirty)

- **about-page-packages** — no `Directory.Packages.props` or `*.csproj` changes since previous anchor
- **guid-reservations** — no `Data/Configurations/**` or `Domain/Constants/**` changes
- **code-analysis-suppressions** — no `Directory.Build.props` / `tests/BannedSymbols.txt` changes

## Pruned

Goal: ~5% reduction (soft), 7% (soft cap). This sweep: **5.80%** — above the soft target, comfortably under the hard cap.

This sweep cleared the **Barrios / CityPlanning plan+spec cluster** plus two small migration plans that the previous sweep (PR #819) had explicitly deferred "by line budget" after confirming their wheat was already extracted into living section docs. A read-only prune analyst re-verified all 11 in full against the living targets (`Camps.md`, `CityPlanning.md`, `Governance.md`, `design-rules.md`, `conventions.md`) — **zero un-migrated wheat found**, so no migrations were needed; all 11 are safe husks.

**Husks deleted:**

- `docs/superpowers/plans/2026-03-13-barrios.md` (3,030 lines) — *implementation plan: file lists, entity/enum/EF-config code samples, service skeletons, task checklists. Durable Camps signal (concepts, lifecycle, name-lock auto-log, returning-camp opt-in auto-approve, role-definition catalogue, IFileStorage keying) already in `docs/sections/Camps.md`, including the two wheat-tagged invariants from the (previously deleted) design spec.*
- `docs/superpowers/plans/2026-03-14-barrio-map.md` (2,041 lines) — *implementation plan (entity code, EF configs, SignalR hub, controllers, test block). Durable CityPlanning signal in `docs/sections/CityPlanning.md`: CampPolygon/History model, one-polygon-per-season constraint, append-only history, edit-authz, placement gating, SignalR broadcast, and the wheat-tagged `text`-not-`jsonb` storage rationale.*
- `docs/superpowers/plans/2026-04-04-barrio-map-official-zones.md` (372 lines) — *mechanical: add `OfficialZonesGeoJson` field + upload/delete + admin card + JS layer. Result (column, admin routes, client-side overlap detection) already in CityPlanning.md.*
- `docs/superpowers/plans/2026-04-04-barrio-map-placement-dates.md` (381 lines) — *mechanical: add `PlacementOpensAt`/`PlacementClosesAt` fields + form. The only non-obvious fact (informational, NOT enforced) is already noted in CityPlanning.md.*
- `docs/superpowers/plans/2026-04-25-city-planning-import.md` (452 lines) — *mostly an admin-import.js block + modal markup + manual smoke steps. The history-Note conventions are already in CityPlanning.md; the rest is import-tool UX detail.*
- `docs/superpowers/plans/2026-04-26-city-planning-marquee-selection.md` (233 lines) — *pure frontend: MapboxDraw custom-mode JS + smoke checklist. No backend/section invariant — code is the spec.*
- `docs/superpowers/specs/2026-03-30-barrio-map-sound-zone-colors-design.md` (59 lines) — *the one surviving system fact (soundZone on the SignalR broadcast) is in CityPlanning.md line 132; the rest is hex-color/rendering presentation detail.*
- `docs/superpowers/specs/2026-04-25-city-planning-import-design.md` (145 lines) — *companion design spec to the import plan; same content at design altitude. Reused endpoints + history-Note conventions already in CityPlanning.md.*
- `docs/superpowers/specs/2026-04-26-city-planning-marquee-selection-design.md` (79 lines) — *design spec for the marquee vertex-selection frontend feature; entirely client-side editor interaction, no durable invariant.*
- `docs/superpowers/plans/2026-04-15-governance-migration.md` (165 lines) — *Governance repo/store/decorator migration plan (#503). Durable outcomes fully reflected in `docs/sections/Governance.md` + `design-rules.md` §4–§5/§15 (incl. the #533 caching-layer drop). "Reference template" framing is historical — section docs are the live reference.*
- `docs/superpowers/plans/2026-04-22-di-split-by-section.md` (108 lines) — *split `AddHumansInfrastructure` into per-section extension files. The resulting `Extensions/Sections/` layout IS the spec and is referenced by `design-rules.md` §5 (line 112) / §2b.*

**Wheat migrated:** none — the cluster was pre-extracted by sweeps PR #819 and earlier; this sweep re-verified and deleted the husks.

**Inbound refs:** the only live references to the deleted plans were in `docs/freshness/last-report.md` (overwritten this sweep). The `<!-- wheat: ... -->` provenance comments in `Camps.md` / `CityPlanning.md` point to the previously-deleted **design specs** (`specs/2026-03-13-barrios-design.md`, `specs/2026-03-14-barrio-map.md`), not the **plan** files deleted here — they are intentional provenance breadcrumbs and were left untouched.

### Deferred to next sweep (under hard cap)

Previously-vetted chaff that didn't fit this sweep's 7% budget — apply on a future sweep:

- `docs/superpowers/plans/2026-04-20-pr235-cache-collapse.md` (1,702) + spec `2026-04-20-pr235-cache-collapse-design.md` (261) — §15 cache pattern documented in `design-rules.md` §15, pinned by `ProfileArchitectureTests`
- `docs/superpowers/plans/2026-04-04-communication-preferences-redesign.md` (1,130) — fully covered by `docs/features/profiles/communication-preferences.md`
- `docs/superpowers/plans/2026-04-20-user-guide.md` (1,201) — `docs/guide/` + `docs/sections/Guide.md` capture everything. **Blocker:** `docs/architecture/maintenance-log.md:26` cites this plan path; deleting it creates a dead link, and maintenance-log is hand-maintained (never touched by the sweep). Peter must fix that ref first, then this can be dropped.
- Old ticket-vendor design docs (`plans/2026-03-15-ticket-vendor-integration.md` 3,258, `specs/2026-03-15-ticket-vendor-integration-design.md` 397) — feature is live (`docs/features/tickets/ticket-vendor-integration.md`); needs a wheat-extraction pass before deletion, not a blind drop

### Kept (had open items)

- `docs/architecture/tech-debt-2026-04-23.md` — multiple `[OPEN]` items remain; not a prune candidate

## Proposed for review

None. The prune analyst found zero medium-confidence wheat — all 11 husks were confirmed fully pre-extracted against the current living docs.

## Editorial drift review (performed, not punted)

All 36 triggered editorial docs were read against the *specific* source files that changed this sweep, and corrected in place where the prose was factually contradicted. The dominant driver was the Camps lead-authorization refactor (lead/event-manager authority moved entirely off the legacy `CampLead`/`camp_leads` onto `CampRoleAssignment` → `CampInfo.IsLead`/`IsEventManager`, retirement now tracked by #774 not #753) and the cross-section read-split (Store/CityPlanning/Notifications/Containers/OnsiteRoster now inject `ICampServiceRead`; several `ICampService` lead/pending/display methods removed).

**Corrected (11 docs):**

- `docs/sections/Camps.md` — lead/event-manager authz rewritten to read-model (`CampInfo.IsLead`/`IsEventManager`, `CampSeasonInfo.LeadUserIds`); legacy `CampLead` no longer consulted for authz (#774); `AddCampMemberAsLeadAsync`→`AddCampMemberToActiveSeasonAsync`; account-merge `ReassignAssignmentsToUserAsync`→`ReassignAsync` (re-FKs `CampRoleAssignment.CampMemberId`, removed the stale "CampMember not folded" gap note); Containers/Profiles/internal cross-section deps moved to `ICampServiceRead`/`ICampRoleCampAccess`/`ICampInfoInvalidator`; caching-decorator + leads-invariant + touch-and-clean prose updated
- `docs/features/camps/camps.md` — `AddMemberAndAssignRoleAsync`→`AddMemberAndAssignRoleInActiveSeasonAsync` (bare overload now private)
- `docs/sections/CityPlanning.md` + `docs/sections/Containers.md` — Camps dependency narrowed to `ICampServiceRead`; removed-method lists replaced with the actually-called set + `CampInfo.IsLead` LINQ
- `docs/sections/Store.md` + `docs/features/store/store.md` — `ICampService`→`ICampServiceRead`; `GetCampLeadSeasonIdForYearAsync`/`GetCampSeasonDisplayDataForYearAsync` replaced with `GetCampsForYearAsync` + read-model helpers
- `docs/sections/Notifications.md` — per-lead pending meter: removed `ICampService.GetPendingMembershipCountForLeadAsync`, now `ICampServiceRead.GetSettingsAsync` + `GetCampsForYearAsync` derived in-memory
- `docs/features/auth/magic-link-auth.md` + `docs/guide/Onboarding.md` — first-login signup now collects a burner name **and** first/last legal name (all required), not a single display name (`CompleteMagicLinkSignupAsync` signature + `CompleteSignup.cshtml` form)
- `docs/features/mailer/audience-debug-screen.md` — debug-table page sizes `20/50/100/200`, default `20` (was `50/100/200`, default `50`)
- `docs/guide/Budget.md` — added the new user-visible **VAT/IVA-inclusive amount** convention (gross figures; VAT% records the rate only, never adds on top) now shown on the Budget/Finance detail views

**Reviewed, no drift (25 docs):** the triggering code changes were internal refactors with no documented/user-visible contradiction — `Auth`, `Onboarding`, `Users`, `Tickets`/`Store`/`CityPlanning`/`Camps` guides, `authentication`, `contact-accounts`, `preferred-email`, `workspace-account-provisioning`, `notification-inbox`, `gdpr-export`, `event-participation`, `ticket-vendor-integration`, `google-removal-notifications` (resx delta touched only `CompleteSignup_*` keys), `guide/{Admin,Events,Profiles}`, etc.

### Open questions for Peter (judgment calls + pre-existing drift found during review)

These were deliberately **not** edited (out of this sweep's "drift caused by recent changes" scope, or a rule-doc judgment call) — your call:

1. **`design-rules.md` §9** — the CORRECT cross-service example injects `ICampStore`, but stores were retired project-wide (§4–5 / §15i say "0 stores exist today"). Pre-existing drift. Drop/replace the example?
2. **`design-rules.md` §8** table-ownership map — Camps row omits `CampRoleService` and the `camp_role_definitions` / `camp_role_assignments` / `camp_members` tables (all accessed via the consolidated `ICampRepository`). Pre-existing. Add them?
3. **`docs/sections/Camps.md`** read/write-split paragraph still describes `ICampService` as exposing "per-user/lead/membership reads" generally — several of those concrete methods were removed this sweep. Tighten the general sentence?
4. **`docs/sections/Camps.md`** notes `BarrioEventsController` at `/Barrios/{slug}/Events/*` while the (now-deleted) `ICampService` XML doc referenced `EventsController` at `/Events/Barrio/{slug}/*` — the Events controller was outside this sweep's changed-file scope, so the route-alias discrepancy is unverified. Worth a follow-up check.
5. Pre-existing, left untouched: `CityPlanning.md` lists `IProfileService` / un-suffixed `ITeamService`/`IUserService` (constructor already uses the `*Read` forms, no `IProfileService`); `Containers.md` dep list still names the unused `GetCampsWithLeadsForYearAsync`.

> **Process note:** SKILL.md already mandated "the default is fix, not flag" for editorial drift — but the rule was buried in prose after the Phase 5 table, so the happy path read "flag-on-change → flag list" and punted (this is the first sweep to actually do the fix). This sweep strengthens `.claude/skills/freshness-sweep/SKILL.md` so the drift-fix is an explicit, mandatory **dispatched** Phase 5 step (with the exact matched-files list handed to each subagent), making the punt path unavailable to future executors.

### Triggered docs reviewed (full list)

<details>
<summary>36 docs</summary>

**Sections (9):** `Auth`, `Camps`, `CityPlanning`, `Containers`, `Notifications`, `Onboarding`, `Store`, `Tickets`, `Users`

**Guide (9):** `Admin`, `Budget`, `Camps`, `CityPlanning`, `Events`, `Onboarding`, `Profiles`, `Store`, `Tickets`

**Features (14):** `auth/authentication`, `auth/magic-link-auth`, `camps/camps`, `city-planning/city-planning`, `global/gdpr-export`, `google-integration/google-removal-notifications`, `google-integration/workspace-account-provisioning`, `mailer/audience-debug-screen`, `notifications/notification-inbox`, `profiles/contact-accounts`, `profiles/preferred-email`, `store/store`, `tickets/event-participation`, `tickets/ticket-vendor-integration`

**Architecture (4, via broad fallback):** `code-review-rules`, `coding-rules`, `conventions`, `design-rules` — reviewed conservatively; no sweep-caused contradictions (two pre-existing drifts surfaced as questions 1–2 above). These carry a `freshness:flag-on-change` marker but **no `freshness:triggers` header**, so they flag on every `src/**` change — adding scoped triggers would cut the noise.

</details>

## Unmarked editorial (no `freshness:triggers` — please add markers)

18 editorial docs have no freshness markers at all, so they're invisible to the sweep's dirty-matching (neither flagged nor auto-updated). Adding `freshness:triggers` + `freshness:flag-on-change` headers brings them into coverage:

- `docs/features/26-events.md`, `27-guide-browser.md`, `43-google-group-membership-sync.md`, `test-system-reliability.md`
- `docs/features/agent/agent-section.md`
- `docs/features/scanner/scanner-barcode.md`
- `docs/guide/{AiHelper,EmailAccount,SigningIn,TicketTransfers,TwoStepVerification,YourData}.md`
- `docs/sections/{Agent,Debug,Events,Mailer,Scanner,admin-shell}.md`

## Questions

See "Open questions for Peter" under the editorial drift review above (5 items: 2 pre-existing `design-rules.md` drifts, a Camps read/write-split sentence to tighten, a Camps event-route alias to verify, and 2 minor pre-existing cross-section-dep nits).

## Skipped (errors)

None this sweep.
