# Freshness sweep report

**Run:** 2026-06-10 (UTC)
**Mode:** diff
**Previous anchor:** `989786372`
**New anchor:** `upstream/main` @ `523a44c3e`
**Worktree base:** `origin/main` @ `b7c0e2282`
**Dirty:** 11/11 mechanical entries, 113 editorial docs (the TableModel view sweep #932 touched ~100 views, so nearly every doc triggered; semantic drivers were Survey #884, iCal feed #931, gate-terminal login #930, events-admin-edit, and the new HUM0031/HUM0032 analyzers).

## Updated automatically

Mechanical (8 updated, 3 verified-current):

- `dev-stats` — +1 daily row (2026-06-10, script)
- `reforge-history` — +1 daily row (script)
- `docs-readme-index` — indexed `sections/survey.md` + `features/scanner/gate-terminal-login.md`; Scanner description now points at `/Scanner/Tickets`
- `authorization-inventory` — Survey section (SurveyController `[AllowAnonymous]`, SurveyAdminController BoardOrAdmin, SurveysApiController API-key filter), ICalFeedApiController (secret-in-URL), AccountController.GateLogin, TicketsGateAdminController; ScannerController policy corrected to `ScannerAccess`
- `controller-architecture-audit` — 5 new controllers + 2 GateLogin actions; controller count → 87
- `dependency-graph` — ICalFeedService + GoogleTranslationService nodes/edges; linkStyle indices 274..291; UserService fan-in → 56
- `service-data-access-map` — Surveys section (SurveyService + 6 `survey_*` tables), ICalFeed (pure fan-out orchestrator, no owned tables), GoogleTranslationService; GDPR contributor list updated
- `data-model-index` — Survey row (6 entities); `SyncServiceSettings.UpdatedByUser` nav ref → `UpdatedByUserId`; Survey cross-section FK entries
- `about-page-packages` — verified current, no change (all 45 packages match `Directory.Packages.props`)
- `guid-reservations` — verified current, no change (block `0004` gate-terminal already present)
- `code-analysis-suppressions` — verified current, no change (HUM0031 went to `WarningsNotAsErrors`, outside the NoWarn block)

Editorial drift-fix (26 docs changed across 14 cluster subagents):

- `sections/Tickets.md` — MatchedUser navs on TicketOrder/TicketAttendee recorded as stripped (no longer "target"); ITicketRepository description updated; transfer cache-invalidation trigger now covers all four lifecycle transitions (create/cancel/reject/approve)
- `sections/Auth.md` — Survey added to MembershipRequiredFilter exempt list
- `sections/Users.md` — EventParticipation.User forward nav removed; GateLogin routes added
- `features/auth/authentication.md` — User entity diagram: stripped navs removed, MagicLinkSentAt added
- `sections/Shifts.md` — ShiftSignupService implements ICalendarFeedContributor (iCal feed #931)
- `sections/Events.md` — CampEventsViewComponent → EventsCardViewComponent; ToggleCampFavourite → ToggleCardFavourite (`POST /Events/Card/Favourite/{eventId}` with returnUrl); EventService implements ICalendarFeedContributor
- `sections/Store.md` + `features/store/store.md` — Admin Summary reprices Open orders to live catalog (matches order page, #937)
- `sections/GoogleIntegration.md` — SyncServiceSettings.UpdatedByUser nav removal; GoogleTranslationService/IGoogleTranslationClient registered in owning-services/external-API/connector lists
- `sections/LegalAndConsent.md` — ConsentRecord.User nav recorded as stripped (3 places)
- `sections/Governance.md` — CastBoardVoteAsync controller switch: NotSubmitted arm collapsed into default
- `sections/Camps.md` + `sections/CityPlanning.md` — CreatedByUser/ReviewedByUser/LastModifiedByUser/ModifiedByUser navs recorded as stripped
- `features/onboarding/volunteer-status.md` — Survey added to exempt-controllers list
- `sections/admin-shell.md` — Gate terminal in TicketAdmin sidebar row; Surveys in Board/Governance row
- `features/global/background-jobs.md` — SendSurveyReminderJob added to catalog
- `features/global/gdpr-export.md` — SurveyResponses GDPR section added (16 contributors)
- `features/global/global-search.md` — IEventService → IEventServiceRead
- `features/debug/client-stats.md` — Bots breakdown table added
- `features/guide/in-app-guide.md` — guide file count 17 → 28
- `sections/Notifications.md` — dropped cross-domain navs; meter-provider dependency refresh
- `sections/AuditLog.md` + `features/audit-log/audit-log.md` — GateTerminalPasswordSet + Survey AuditAction values
- `docs/architecture/design-rules.md` — §15i repository inventory 33 → 34 (+ SurveyRepository row)
- `docs/architecture/roslyn-analysis.md` — next-free analyzer id → HUM0033; shipped range → HUM0024–HUM0032
- `docs/seed-data.md` — gate-terminal well-known-account seeding pattern (GateTerminalAccountSeeder / SystemUserIds.GateTerminal)

Verified clean (no drift): `sections/survey.md` (full check of new section doc against landed code), Calendar/Cantina/Profiles/Issues/Feedback/Campaigns/guide trees — their matched changes were mechanical refactors (TableModel sweep, nav strips, extract-method).

## Pruned

**Wheat migrated:**
- `docs/superpowers/plans/2026-05-10-early-entry-camps.md` §Task 11 → `docs/sections/Camps.md` (Membership invariants): RejectCampMemberAsync intentionally passes `cascadeRoleAssignments: false` — Pending members can hold no role assignments. Verified against `CampService.cs:1212`.

**Husks deleted:**
- `docs/superpowers/plans/2026-05-10-early-entry-camps.md` (1,689 lines) — feature shipped (#490); all design decisions live in the surviving spec `2026-05-10-early-entry-camps-design.md`; section invariants already in `Camps.md`; remainder was task lists, TDD steps, and code samples now in `src/`. Deferred from the previous sweep by budget; processed this sweep.

**Inbound refs:** none needed retargeting (the only ref was in the prior `last-report.md`, replaced by this report).

**Budget:** total docs 82,453 lines; 5% target 4,122; 7% cap 5,771; deleted 1,689 (~2.0%). Under target because the allowlist is exhausted: no `docs/plans/` file is >30 days old, no spec is >60 days old, and `tech-debt-2026-04-23.md` still has `[OPEN]` items (ineligible). Largest next-sweep candidates: the 2026-05-12/13/14 `docs/plans/section-align-*.md` files (eligible from 2026-06-12).

## Flagged for human review

All items below were delivered to Peter inline at the end of the sweep (Phase 7.5); resolutions are recorded next to each.

1. `features/auth/magic-link-auth.md` — describes `User.NormalizedEmail` / `FindByEmailAsync` fallback; code uses `IUserEmailService.FindVerifiedEmailWithUserAsync`. Historical-spec language vs current-state. — *pending*
2. `features/tickets/ticket-vendor-integration.md` — `/Tickets/GateList` still "Stub for June implementation"; gate lookup shipped at `/Scanner/Tickets` (#930). — *pending*
3. `sections/Holded.md` — "v1 ships only the four methods" stale; Feature 2 added GetContactAsync/ListChartOfAccountsAsync/ListPaymentsAsync/UpsertContactAsync. — *pending*
4. `features/profiles/contact-fields.md` — uses `LeadsAndBoard = 1`; enum is `CoordinatorsAndBoard = 1`. — *pending*
5. `features/profiles/preferred-email.md` — documents removed `User.GoogleEmail` / `GetEffectiveEmail()` / `GetGoogleServiceEmail()`; canonical reads are FullProfile-based (#635). — *pending*
6. `features/profiles/dietary-medical-nudge.md` — dietary/medical fields moved from VolunteerEventProfile to Profile; doc still says VolunteerEventProfile columns. — *pending*
7. `features/profiles/profiles.md` — BurnerName shown as `string?`; actually non-nullable `string`. — *pending*
8. `sections/Profiles.md` — "Services.Profile/" should be "Services.Profiles/"; its freshness trigger `src/Humans.Application/Services/Profile/**` matches nothing. — *pending*
9. `features/campaigns/campaigns.md` — wave-send exclusion documented as `User.UnsubscribedFromCampaigns`; actual gate is `ICommunicationPreferenceService.IsOptedOutAsync`. — *pending*
10. `features/expires-on-deadline.md` — `User.NormalizedEmail` deadline row (2026-05-18) is past. — *pending*
11. `features/notifications/notification-inbox.md` — Cleanup section omits the 30-day unresolved-Informational rule; Sources table missing many sources vs section doc. — *pending*
12. `design-rules.md` — ICalendarFeedContributor (#931) is a second cross-section fanout pattern structurally identical to §8a IUserDataContributor, currently undocumented as a pattern. Design decision whether/where to document. — *pending*

## Proposed for review

None — all prune candidates resolved this sweep.

## Questions

The 12 flagged items above were asked inline; this section will be updated with resolutions.

## Skipped (errors)

None — all 11 mechanical entries and all 14 editorial cluster subagents completed.
