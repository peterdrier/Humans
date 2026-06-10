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

All 12 items were delivered to Peter inline (Phase 7.5); he approved fixing all 12, and all were applied on this PR branch in the follow-up commit.

1. `features/auth/magic-link-auth.md` — `User.NormalizedEmail` / `FindByEmailAsync` fallback. — **fixed**: lookup flow rewritten to `IUserEmailService.FindVerifiedEmailWithUserAsync` throughout diagrams and examples.
2. `features/tickets/ticket-vendor-integration.md` — `/Tickets/GateList` "Stub for June implementation". — **fixed**: note now points at the live `/Scanner/Tickets` gate lookup (#930); GateList remains a placeholder.
3. `sections/Holded.md` — "v1 ships only the four methods" stale. — **fixed**: replaced with the current eleven-method `IHoldedClient` surface.
4. `features/profiles/contact-fields.md` — `LeadsAndBoard` vs enum `CoordinatorsAndBoard`. — **fixed**: all 8 occurrences renamed (plus one in preferred-email.md US-11.4).
5. `features/profiles/preferred-email.md` — removed `User.GoogleEmail` / `GetEffectiveEmail()` / `GetGoogleServiceEmail()` still documented. — **fixed**: stale subsections replaced with the FullProfile-based read path; sync/jobs notes updated.
6. `features/profiles/dietary-medical-nudge.md` — fields documented on VolunteerEventProfile. — **fixed**: Data Model + Cross-section dependencies rewritten — fields live on Profile (VEP columns are retained-only tombstones); saves via `IProfileEditorService.SaveDietaryMedicalAsync`.
7. `features/profiles/profiles.md` — BurnerName nullability. — **fixed**: `string? (256)` → `string (256)`.
8. `sections/Profiles.md` — `Services.Profile` vs `Services.Profiles`. — **fixed**: 3 occurrences corrected including the dead freshness trigger glob (`Services/Profiles/**` now matches).
9. `features/campaigns/campaigns.md` — wave-send exclusion attribution. — **fixed**: now `ICommunicationPreferenceService.IsOptedOutAsync(userId, MessageCategory.CampaignCodes)`; the separate Unsubscribe-route section (which legitimately sets `User.UnsubscribedFromCampaigns`) verified accurate and left alone.
10. `features/expires-on-deadline.md` — `User.NormalizedEmail` deadline 2026-05-18 past. — **fixed**: the `[ExpiresOn]` in `User.cs` was extended to 2026-09-01; table row updated to match (symbol still exists).
11. `features/notifications/notification-inbox.md` — Cleanup rules + Sources table out of sync. — **fixed**: 30-day unresolved-Informational rule + actionable-never-deleted rule added; 8 missing sources added.
12. `design-rules.md` — ICalendarFeedContributor fanout undocumented. — **fixed**: new §8b "Cross-Section Fanout — Contributor Pattern" generalizes the shape (orchestrator owns no tables, sections opt in via contributor interface) and tables both instances (GDPR `IUserDataContributor`, iCal `ICalendarFeedContributor`).

## Proposed for review

None — all candidates resolved this sweep.

## Questions

None pending — all 12 inline questions answered ("fix all 12") and applied.

## Skipped (errors)

None — all 11 mechanical entries and all 14 editorial cluster subagents completed.
