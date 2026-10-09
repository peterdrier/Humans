# section-doctor — Rideshare — 2026-10-09

- Invocation: unattended daily routine (no arguments); routine prompt: skip Phase 8, PR against peterdrier/Humans main, ignore verify-migrations-apply
- Anchor commit: `a5c17fcdd` (origin/main at branch point); branch `section-doctor/2026-10-09T011615Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

Re-doctor against `BASE` `e5bffbfdb`. Since the last run the debt sweeps and the Settings/Contracts folds reworked the section without changing what it does for members: notifications are now rendered in the recipient's language, the snapshot decorator clears in `finally` and refuses to store a snapshot loaded before a write's clear, provider and stored route geometry is validated, and the board list no longer waits on the map. The target was re-derived from that code before any thread ran; its previous version had drifted in three places (English-only notices, the decorator's guard, unvalidated geometry) and most `file:line` cites had moved, so the earlier target was right when written and the section moved.

The section is in good shape. No conformance hit, clean resx parity and keys, no open issues in either repo. The run's strikes are doc truth (one invariant claimed "only" where the code also retries an unrouted edit), one test for an untested refusal path, and one one-caller helper inlined. Two behaviour questions go to Peter.

Independence check: pass. Findings 1, 11 and 12 came from the target re-derivation and the Behavior walk, not from a tool or grep.

## Findings

1. `Rideshare.md`'s route invariant and Triggers, and the feature spec's US-1, say a route is recomputed only on create or a geometry change; `RideshareService.UpdateOfferAsync` also recomputes when no route was stored (a retry after a provider outage).
2. `Rideshare.md` ## Invariants opened with debt-sweep bullets in change-language ("unchanged", "existing"), unbolded, split by blank lines, one of them (the breadcrumb landmark) a global localization rule rather than a section invariant.
3. `Rideshare.md` ## Cross-Section Dependencies labelled `ISettingsService` as **Shifts**.
4. `Docs/features/rideshare-board.md` said the trip's driver may withdraw an interest; the code allows the posting owner, who is the rider on a pin answer.
5. `authorization.md` said every ownership refusal is the service's `UnauthorizedAccessException`; the offer and request edit forms also refuse a non-owner in the controller.
6. `ExpressInterestAsync` refusing a closed pin (`Rideshare_Error_RequestClosed`) and a driver answering their own pin (`Rideshare_Error_OwnRequest`) had no test on the express path.
7. Comments: the cuts the Comments thread proposed (divider banners, `// Navigation properties`, the `§15b` reference) are conventions shared with other sections; kept.
8. `RideshareService.DisplayNameAsync` was a two-line private helper with one caller after the notification rework.
9. `Rideshare.md`'s `freshness:triggers` did not watch the cross-section contracts its Cross-Section Dependencies describe.
10. The dated design record names pre-split paths and collaborators in §13–14 with no pointer to where the current shape lives.
11. `RequestView.IsMatched` is true when the request's owner has any accepted interest on any active trip, whatever its direction or date: a rider with an inbound and an outbound request, matched only inbound, sees both badged Matched on Mine and drops out of the admin's riders-still-looking count.
12. When a rider accepts a driver's answer to their pin, the driver is notified with `Rideshare_NoticeAccepted` ("You're in: ride with {rider}"), wording written for a rider.
13. Ledger row RIDE-2 (rule exceptions carry resource keys) still holds.
14. Controller, routing-client and service tests assert log level, text and the absence of an exception; the debt sweeps added them to pin a fix `Rideshare.md` now states as an invariant. Kept.
15. `CachingRideshareServiceTests.ContributeForUser_ForwardsToTheInner` and `GetActiveYear_PassesThrough` only check forwarding. Kept: they pin the GDPR wiring through the decorator.
16. The target's S4 row said every write redirects to Mine; an interest POST naming no trip returns to the board.

## Debt verified

- RIDE-2 — still-true — the service still throws `RideshareRuleException` with resource keys and both controllers localize `ex.Key` — `src/Sections/Humans.Rideshare/Services/RideshareRuleException.cs:9`, `src/Sections/Humans.Rideshare/Controllers/RideshareController.cs:226`, `src/Sections/Humans.Rideshare/Controllers/RideshareAdminController.cs:54`

Every open row was verified; the cap was not reached.

## Worked

- Finding 1: route-retry claim corrected in `Rideshare.md` (invariant and Triggers) and `Docs/features/rideshare-board.md`.
- Finding 2: debt-sweep invariant bullets bolded and rewritten in present tense; the breadcrumb bullet dropped; "unchanged" and "existing" cut.
- Findings 3, 4, 5: label, withdraw rule and the edit-form 403 corrected.
- Finding 6: `ExpressInterest_AnsweringAPin_RefusesAClosedPinAndTheDriversOwnPin` added in `tests/Humans.Rideshare.Tests`.
- Finding 8: `DisplayNameAsync` inlined into `NotifyAsync`.
- Finding 9: `Rideshare.md` triggers now watch `IAuditLogService`, `ISettingsService`, `INotificationEmitter`, `IUserMerge` and `IUserDataContributor`.
- Finding 10: one Status sentence in the design record points at `Rideshare.md`.
- Finding 16: target S4 row reworded.
- Target re-derived (parts 3–6 and the load-bearing list), every invariant re-cited.

## Skipped

- Sections passed over as blocked: Budget (open PR), Events (open PR), Tickets (recent branch).
- Finding 7: banners, navigation-property markers and the `§15b` reference are conventions across sections; striking them here would make Rideshare the odd one out.
- Finding 13: RIDE-2 is a throw-to-result refactor across the service, both controllers and the tests; it stays on the ledger for the debt sweep, whose plan it already carries.
- Findings 14, 15: kept, reasons in the findings.
- Freshness proposed triggers on `authorization.md` and `data-access.md`; both are mechanical outputs of the freshness catalog, so the triggers were not added.
- Prose & surface flagged the admin views' literal English; `RideshareAdminOrAdmin` gates them, so `memory/code/localization-admin-exempt.md` applies.
- Test gaps on the board feed carrying no interest data and the API key never reaching a log line would be absence assertions (`memory/architecture/no-tests-for-absences.md`).

## Retro

**Selector.** Rideshare was a fair pick: a month of churn, all of it other runs' work (debt sweeps, two contract folds), none of it reviewed against the section as a whole.

**Wasted motion.** The reforge surface score first ran against an unrestored solution and refused to print; it had to wait for a full build. The haiku Conformance dispatch classified an empty result set.

**What striking revealed.** The Freshness thread's trigger proposal for the two mechanical docs repeated the same mistake the previous Rideshare run recorded; it was caught only by re-reading that run's Needs-Peter list.

**Target diff.** The section moved: notification localization, the decorator's stale-read guard and geometry validation are new since the last target, and the earlier target was right for the code it described.

## Needs Peter

- [ ] 11 — should a request count as matched only by an accepted interest on a trip going its direction on its date?
- [ ] 12 — on a pin answer the rider accepts, send the driver its own wording (new `Rideshare_Notice*` key) rather than "You're in: ride with {rider}"?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Rideshare/Controllers/RideshareAdminController.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Controllers/RideshareApiController.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Controllers/RideshareController.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareInterestConfiguration.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareRequestConfiguration.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareSettingsConfiguration.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareTripConfiguration.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/IRideshareRepository.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Migrations/20260909233307_InitialRideshare.Designer.cs` | generated |
| `src/Sections/Humans.Rideshare/Data/Migrations/20260909233307_InitialRideshare.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Migrations/RideshareDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Rideshare/Data/RideshareDbContext.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/RideshareDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/RideshareRepository.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Docs/2026-06-14-rideshare-section-design.md` | changed |
| `src/Sections/Humans.Rideshare/Docs/Rideshare.md` | changed |
| `src/Sections/Humans.Rideshare/Docs/authorization.md` | changed |
| `src/Sections/Humans.Rideshare/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.Rideshare/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.Rideshare/Docs/features/rideshare-board.md` | changed |
| `src/Sections/Humans.Rideshare/Docs/health.md` | changed |
| `src/Sections/Humans.Rideshare/Domain/CostSharing.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/InterestStatus.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/LuggageSize.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/RequestStatus.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/RideshareDirection.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/RideshareInterest.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/RideshareRequest.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/RideshareSettings.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/RideshareTrip.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/TripStatus.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Domain/VehicleType.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Humans.Rideshare.csproj` | reviewed |
| `src/Sections/Humans.Rideshare/Models/AdminViewModels.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/BoardFeatureCollection.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/BoardViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/MineViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/OfferFormViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/RequestFormViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/RideshareDates.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.ca.resx` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.cs` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.de.resx` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.es.resx` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.fr.resx` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.it.resx` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.resx` | reviewed |
| `src/Sections/Humans.Rideshare/Section.cs` | reviewed |
| `src/Sections/Humans.Rideshare/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.Rideshare/SectionNav.cs` | reviewed |
| `src/Sections/Humans.Rideshare/SectionPolicies.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/CachingRideshareService.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/IRideshareService.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/RideshareReadModels.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/RideshareRuleException.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/RideshareService.cs` | changed |
| `src/Sections/Humans.Rideshare/Services/Routing/IRouteProvider.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/Routing/OpenRouteServiceClient.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/Routing/RouteProviderOptions.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/Routing/RoutingGeometry.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Index.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Mine.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Offer.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/RideRequest.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/_InterestRow.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/RideshareAdmin/Day.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/RideshareAdmin/Index.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/_ViewImports.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/wwwroot/js/rideshare/board.js` | reviewed |
| `src/Sections/Humans.Rideshare/wwwroot/js/rideshare/pick-point.js` | reviewed |
| `tests/Humans.Rideshare.Tests/Browser/board.test.cjs` | reviewed |
| `tests/Humans.Rideshare.Tests/Browser/pick-point.test.cjs` | reviewed |
| `tests/Humans.Rideshare.Tests/Controllers/RideshareControllerTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Humans.Rideshare.Tests.csproj` | reviewed |
| `tests/Humans.Rideshare.Tests/Infrastructure/RideshareTestHarness.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Models/BoardAndDayViewModelTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Models/BoardFeatureCollectionTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Models/FormViewModelTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Models/MineViewModelTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/RideshareArchitectureTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Services/CachingRideshareServiceTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Services/OpenRouteServiceClientTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Services/RideshareServiceTests.cs` | changed |
| `tests/Humans.Rideshare.Tests/Services/RideshareSnapshotTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main |  | 0 (target re-derived) |
| Behavior & bugs | main |  | 2 (11, 12) |
| Freshness | subagent (`doctor-reader`) | opus-low | 8 (1–5, 9, 10, 16) |
| Conformance | subagent (`general-purpose`) | haiku | 0 |
| Tests | subagent (`doctor-reader`) | opus-low | 3 (6, 14, 15) |
| Prose & surface | subagent (`general-purpose`) | haiku | 3 raw, none kept: admin views exempt from localization |
| History | subagent (`doctor-reader`) | opus-low | 0 (one dispatch with Comments) |
| Comments | subagent (`doctor-reader`) | opus-low | 2 (7, 8) |
| Inbox | subagent (`doctor-reader`) | opus-low | 1 (finding 13) |

