# Freshness Sweep — 2026-05-28

| | |
|---|---|
| Previous anchor | `1c4c6ad2` |
| New anchor | `cdd850bd` |
| Mode | diff |
| Mechanical entries dirty | 10 of 11 |
| Editorial flag-on-change triggered | 68 |
| Editorial unmarked (review needed) | 22 |
| Husks pruned | 4 files (−9,164 lines) |
| Docs tree | 131,711 → 122,547 lines (−6.96%) |

## Updated automatically

- **dev-stats** — appended today's row (`docs/development-stats.md`)
- **reforge-history** — appended today's row (`docs/reforge-history.csv`)
- **docs-readme-index** — indexed 64 features / 33 sections / 25 guide docs; added missing `features/profiles/public-coordinator-popover.md` entry
- **authorization-inventory** — re-scanned ~210 `[Authorize]` attributes across 80 controllers, 11 resource-based handlers, 4 composite handlers, ~50 `AuthorizeAsync` call sites; folded in Events Guide controllers' migration from `Roles=RoleGroups.EventsAdminOrAdmin` → `Policy=PolicyNames.EventsAdminOrAdmin`, the corresponding `_Layout.cshtml` flag refactor, removed Cantina nav link, `AdminDetail.cshtml` isAdmin flag refactor, `_GuideLayout.cshtml` authorize-policy fix, updated `StoreController`/`ProfileController` call-site line numbers
- **controller-architecture-audit** — refreshed 80 controllers; added 5 new actions (`ProfileController.PublicPopover`, `StoreController.CreateTeamOrder` + `Delete`, `VolunteerTrackingController.SetAvailabilityDay` + `ClearAvailabilityDay`); 0 new rename suggestions
- **dependency-graph** — 84 services scanned, 278 edges (262 eager incl. pending dashed, 16 lazy). Added 5 eager edges: Store→Team, DriveMon→User, ShiftSign→BurnSettings, RotaMsg→Team, Workload→ShiftView; added `UserEmailProviderBackfillService` node (→Audit). Updated fan-in counts: User 53→54, Team 26→28, Audit 34→35. Reindexed lazy `linkStyle`
- **service-data-access-map** — refreshed against repo consolidations (#806/#809/#810/#811): Profiles 3-repos and CampRole repo folded into `IUserRepository`/`ICampRepository`, Shift mgmt+signup back to one `ShiftRepository` class; cross-section violations table now lists 4 remaining (`GoogleSyncOutboxEvents`, Shifts-internal `EventSettings`/`ShiftSignups` under HUM0025, `SystemSettings` disjoint-keys)

### Already current — no change needed

- **data-model-index** — 95 entities verified against `src/Humans.Domain/Entities/`; all already present in the entity-index table
- **guid-reservations** — 6 blocks / 26 GUIDs across `SystemTeamIds.cs` + 5 configuration files; no additions/removals
- **code-analysis-suppressions** — 10 suppressions indexed (8 root `NoWarn` + 2 test `NoWarn`); current block already matches `Directory.Build.props` and `tests/Directory.Build.props`

### Skipped (not dirty)

- **about-page-packages** — no `Directory.Packages.props` or `*.csproj` changes since previous anchor

## Pruned

Goal: ~5% reduction (soft), 7% (hard cap). This sweep: 6.96%.

- `docs/superpowers/plans/2026-04-21-agent-section-phase-1.md` (4,384 lines) — *all chaff: Agent Phase 1 ships; durable Agent wheat (10 invariants, data model, tool API, cross-section boundaries) already in `docs/sections/Agent.md`. Maintenance-log notes 2026-04-21 'Agent section Phase 1'.*
- `docs/superpowers/plans/2026-04-26-issue-489-camp-roles.md` (3,720 lines) — *all chaff: implementation complete; per-camp-role wheat (CampRoleDefinition/Assignment entities, SpecialRole enum, slot semantics, cascade rules, audit triggers, IGoogleGroupMembershipSource integration, scope invariant) all in `docs/sections/Camps.md`.*
- `docs/superpowers/plans/2026-04-19-volunteer-coordinator-dashboard.md` (559 lines) — *all chaff: 9 analytics methods, ShiftDashboardAccess policy, 5-min cache + invalidation, period/date-range mutex, sub-team unfolding — all captured in `docs/sections/Shifts.md`.*
- `docs/superpowers/plans/2026-03-30-barrio-map-sound-zone-colors.md` (501 lines) — *all chaff: MapLibre layer config + hex color mappings are pure rendering implementation, all code in `src/Humans.Web/wwwroot/js/city-planning/`.*

### Deferred to next sweep (sized to stay under hard cap)

These dropped-entirely candidates also passed the misc/barrios batch reviews but didn't fit this sweep's budget. Apply on a future sweep:

- `docs/superpowers/plans/2026-04-20-pr235-cache-collapse.md` (1,702) — §15 cache pattern is now Profiles' reference impl; documented in `design-rules.md` §15 and pinned by `ProfileArchitectureTests`
- `docs/superpowers/plans/2026-04-20-user-guide.md` (1,201) — `docs/guide/` + `docs/sections/Guide.md` already capture everything. **Inbound ref:** `docs/architecture/maintenance-log.md:26` cites the plan path; will become a dead link — fix manually when this is dropped (maintenance-log is in the "never touched" freshness set)
- `docs/superpowers/plans/2026-04-04-communication-preferences-redesign.md` (1,130) — `docs/features/profiles/communication-preferences.md` covers all 8 categories, lock rules, deprecation history, schema
- `docs/superpowers/plans/2026-04-15-governance-migration.md` (165) — wheat in `docs/sections/Governance.md`
- `docs/superpowers/plans/2026-04-22-di-split-by-section.md` (108) — `src/Humans.Web/Extensions/Sections/` layout is the spec
- Barrios/CityPlanning cluster (all confirmed pure chaff by analysis but deferred): `2026-03-13-barrios.md` (3,030), `2026-03-14-barrio-map.md` (2,041), `2026-04-04-barrio-map-official-zones.md` (372), `2026-04-04-barrio-map-placement-dates.md` (381), `2026-04-25-city-planning-import.md` (452), `2026-04-26-city-planning-marquee-selection.md` (233), specs `2026-03-13-barrios-design.md` (412), `2026-03-14-barrio-map.md` (321), `2026-03-30-barrio-map-sound-zone-colors-design.md` (59), `2026-03-15-ticket-vendor-integration-design.md` (397)

### Kept (had open items)

- `docs/architecture/tech-debt-2026-04-23.md` (227 lines) — multiple `[OPEN]` items remain (AdminController AudienceSegmentation DbContext reads, AuditLogRepository cross-domain reads, UserRepository.PurgeAsync, AccountMergeRepository .Include chains, FeedbackService IFormFile/IHostEnvironment violation, GoogleWorkspaceHealthCheck direct Google.Apis imports, UserService AnonymizeExpiredAsync lazy-IServiceProvider, UserService.PurgeAsync TransactionScope, ViewComponents IMemoryCache injection, ProcessGoogleSyncOutboxJob catch GoogleApiException, TeamService re-fetch)

## Proposed for review

Medium-confidence wheat — analysis agents flagged for human decision before drop:

1. **Auto-approval rule for returning camp seasons** — Spec `2026-03-13-barrios-design.md` §"Returning Camp Season Opt-In" states: *"If the barrio has any prior season that was NOT Rejected, the new season is auto-approved → status: Active immediately. Otherwise, status: Pending (requires CampAdmin review)."* Not explicitly stated in `docs/sections/Camps.md`. If still implemented in `CampService`, one bullet under Camps.md → Triggers/Invariants would let the spec drop cleanly.
2. **Name lock auto-history-log trigger** — Spec `2026-03-13-barrios-design.md` §"Name Lock": *"When any name change occurs, the old name is auto-logged to BarrioHistoricalName with Source = NameChange."* `docs/sections/Camps.md` mentions `NameLockDate`/`NameLockedAt` fields but not the auto-log trigger. One-line addition would close the gap.
3. **GeoJSON stored as text not jsonb** — Spec `2026-03-14-barrio-map.md` §Storage: *"GeoJSON is stored as PostgreSQL text columns (not jsonb) — the system never queries inside the JSON structure."* Narrow but durable. `docs/sections/CityPlanning.md` already shows `text` in the data-model tables; rationale may be worth a sentence.

## Flagged for human review (editorial `freshness:flag-on-change`)

68 editorial docs flagged by their `freshness:flag-on-change` markers because their trigger paths fired this sweep. These need a human review to decide whether content needs updating:

<details>
<summary>Full list (68 docs)</summary>

**Sections (16):** `AuditLog`, `Auth`, `Budget`, `Campaigns`, `Camps`, `GoogleIntegration`, `Issues`, `LegalAndConsent`, `Onboarding`, `Profiles`, `Shifts`, `Store`, `Teams`, `Tickets`, `Users` + `seed-data.md`

**Guide (14):** `Admin`, `Budget`, `Campaigns`, `Camps`, `Email`, `Events`, `GoogleIntegration`, `LegalAndConsent`, `Onboarding`, `Profiles`, `Shifts`, `Store`, `Teams`, `Tickets`

**Features (38):**
- `47-volunteer-tracking`
- `expires-on-deadline`
- `auth/authentication`, `auth/magic-link-auth`
- `budget/budget`
- `campaigns/campaigns`
- `camps/camps`
- `cantina/daily-roster`
- `email/email-flag-violations-remediation`
- `global/administration`, `global/gdpr-export`
- `google-integration/{drive-activity-monitoring,google-integration,google-removal-notifications,workspace-account-provisioning}`
- `governance/membership-tiers`
- `issues/issues-system`
- `legal-and-consent/legal-documents-consent`
- `onboarding/onboarding-pipeline`, `onboarding/volunteer-status`
- `profiles/{communication-preferences,contact-accounts,contact-fields,dietary-medical-nudge,preferred-email,profile-pictures-birthdays,profiles,public-coordinator-popover}`
- `shifts/{coordinator-roles,shift-management,shift-preference-wizard,shift-signup-visibility}`
- `store/store`
- `teams/{hidden-teams,teams}`
- `tickets/{event-participation,ticket-transfer,ticket-vendor-integration}`

</details>

## Unmarked editorial (no `freshness:triggers` — please add markers)

22 editorial docs have no `freshness:triggers` header, so they were caught only by the broad "src/** changed" fallback. Adding triggers narrows future false-positive flagging:

- `docs/architecture/{code-review-rules,coding-rules,conventions,design-rules}.md`
- `docs/features/26-events.md`, `27-guide-browser.md`, `43-google-group-membership-sync.md`, `test-system-reliability.md`
- `docs/features/agent/agent-section.md`
- `docs/features/scanner/scanner-barcode.md`
- `docs/guide/{AiHelper,EmailAccount,SigningIn,TicketTransfers,TwoStepVerification,YourData}.md`
- `docs/sections/{Agent,Debug,Events,Mailer,Scanner,admin-shell}.md`

## Questions

None this sweep.

## Skipped (errors)

None this sweep.
