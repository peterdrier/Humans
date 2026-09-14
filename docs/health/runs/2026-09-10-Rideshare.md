# section-doctor — Rideshare — 2026-09-10

- Invocation: unattended daily run (cloud), no arguments; Phase 8 skipped per the stored prompt.
- Anchor commit: `b0622ff2` (origin/main at branch point); branch `section-doctor/2026-09-10T011607Z`.
- Budget: standard daily.
- PR: peterdrier/Humans#1647

## Assessment summary

First doctoring of Rideshare, a day after it shipped (never-doctored tier, the only section
left in it). Target shape written fresh to `Docs/health.md`.

The section is in the shape its design record promised: services own the rules, the read side
is one snapshot per year filtered in memory, and nothing reaches past the section's own
tables. What the run found instead sits at the edges of the shape — a flow the target's
visibility invariant exposes (finding 1), a form that refuses silently (finding 2), and a test
project that is thorough on the service and thin one layer out, on the projections and the
controller's error mapping (findings 4–8). Every doc claim checked resolves except the ones in
finding 9, and the section's comments carried citations to a numbering scheme that collides
with `design-rules.md`'s (finding 10).

The headline is finding 1, and it split: the display half is struck, the semantics half is
Peter's. The reviewer gate on that strike surfaced finding 16, the same blind spot on the
other side of the same page.

## Findings (ranked, 3e)

1. **A rider's sent interest shows no sign that its trip was cancelled, and cancelling leaves
   every interest standing with nobody told.** `CancelOfferAsync` flips the trip's status and
   stops; pending and accepted interests keep their state and no notification fires (the
   design record's notification list has create, accept and decline, and cancel is not on it).
   On Mine, the driver's own offer rows show the trip's status badge but the rider's "Interests
   I sent" rows rendered only the interest's status, so an *Accepted* row kept pointing at a
   ride that no longer exists on the board. Split: (a) display — the sent row now shows
   the trip's status badge whenever the trip is not Active, using the existing
   `Enum_TripStatus_*` keys and `EnumBadgeMap`, so no new strings (struck, reviewer-approved);
   (b) semantics — whether a cancel should withdraw the interests and notify accepted riders
   is a behavior change. Needs Peter.
2. **The admin settings form swallowed a blank window date with no message.** A blank date
   fails `[Required]`, the controller's summary error is only added when ModelState was valid,
   and the four date inputs carried no `asp-validation-for` span while the summary is
   `ModelOnly` — the form re-rendered unchanged and silent. Spans added (admin view, no resx).
3. **`IRideshareRepository.DeleteUserRowsAsync`'s doc comment contradicted the repository and
   `Rideshare.md`.** It said other people's answers to an erased person's requests keep their
   trip and lose the request pointer; the repository deletes them, and the section doc says so.
   Comment rewritten to what the code does.
4. **Two cancelled-edit tests passed on any `InvalidOperationException`.** `RideshareRuleException`
   derives from it, so `UpdateOffer_OnACancelledTrip_Throws` and
   `UpdateRequest_OnACancelledRequest_Throws` could not tell the cancelled-edit rule from any
   other rule failure. Tightened to the rule key, as the pin-answer tests already do; the
   window-order settings test had the same shape and got the same fix.
5. **Service rules with no test.** Seats-below-accepted on an offer edit; route recompute only
   when the point, waypoints or direction change (and the retry when no route was stored);
   the owner-only gate on cancel for both offers and requests, and that a second cancel is a
   no-op; withdraw by the posting owner rather than the author, and its refusal once declined;
   the blank-destination refusal on settings. All pinned.
6. **`MineViewModel.Build` and `BoardFeatureCollection.Build` had no tests** although the target's
   §4 cites both as enforcement points. Mine's lists — the received-interest filter that
   excludes pin answers, the driver-answer rows that carry the driver's trip, the counterpart
   resolution on sent rows, the direction-then-date order — and the board's GeoJSON — the
   feature kinds, the stored route used verbatim, the straight-line fallback in travel order
   for both directions, the malformed-route catch, `isMine`, the destination omitted without
   settings. New test files for each.
7. **The form models, `RideshareDates.Parse` and `BoardViewModel.DefaultDate` had no tests.**
   `ToSave` returning null on an unparsable date, the waypoint split with trimming and blank
   lines dropped, the `FromTrip`/`FromRequest` → `ToSave` round trip, the profile prefill and
   the window-start default in `ForNew`, ISO-only date parsing. New test file.
8. **The controller's error-contract helpers had no tests.** `RunThenMineAsync` and
   `SaveFormAsync` are the only logic in `RideshareController` — 404 and 403 pass through, a
   rule becomes a toast on Mine or a model error on the form — and no controller was
   instantiated anywhere in the test project. The Camps controller-test pattern (claims
   principal, `TempDataDictionary`, substitutes) fit; it needed the shared framework reference
   the Camps test project already carries, added to this section's. New test file, including
   the anonymous challenge and the empty-trip redirect back to the board.
9. **Doc drift.** `Rideshare.md`'s data model omitted the template's *Indexes / constraints* line
   per entity though every index fact was known; its freshness trigger set covered only the
   section's own tree while the doc asserts about `RoleNames`, `PolicyNames`,
   `NotificationSource`, `AuditAction` and the architecture test; `rideshare-board.md`'s US-5
   and US-6 named the actor as Admin where the gate is `RideshareAdminOrAdmin` and the section
   doc makes RideshareAdmin the actor. All fixed. The Freshness thread also proposed trigger
   blocks on `authorization.md` and `data-access.md`; not done — see finding 17.
10. **Dangling `design §N` citations and restating comments.** The csproj, `RideshareResource.cs`
    and `_ViewImports.cshtml` cited the G5 migration checklist as `design §5`, `§7a`, `§3`,
    `§1/§2`, which resolve to unrelated sections of `design-rules.md`; `Mine.cshtml` carried
    a divider above each heading repeating the heading's words; a configuration noted the
    absence of a seed; the repository and the caching decorator restated their own code at
    length. Citations cut with the sentences kept, dividers deleted, blocks shortened.
    `Section.cs`'s `(§15b)` stays — it resolves to *15b. Repository Rules* and states a live
    DI constraint (the Comments and History threads disagreed on it; the citation wins).
11. **The inner `RideshareService` declared `IUserMerge`** while only the caching decorator is
    registered as the section's merge hook, and the inner did not declare the export
    contributor marker it equally forwards. The declaration was the one place the merge and
    export markers were treated differently. Dropped; `ReassignAsync` stays on `IRideshareService`.
12. **The target shape itself, first draft.** The structure tree wrote `wwwroot/js/` for
    `wwwroot/js/rideshare/`; §2's S2 said "one expression of each step per posting kind" while
    the controller carries the offer and request pipelines as two hand-written copies, and the
    honest target is that the copies stay step for step the same (a generic helper for two
    short pipelines reads worse — recorded in §6); the OpenRouteService-over-Google rationale
    had no home outside the design record and is now in the weirdness block. Fixed before the
    first commit.
13. **`docs/freshness/last-report.md:155` still calls the design record "not scheduled for
    build".** A dated sweep report; the design record itself reads *Implemented*. Note only.
14. **The design record is the only home of some facts** — the routing-provider rationale, the
    stats-view kill criterion, the naming rationale, the non-goals. Deletion not proposed; the
    provider rationale is now also in `health.md` §6.
15. **Conformance, Prose & surface, and the Tests thread's keep verdicts found nothing actionable.** The
    architecture rules ran clean, razor lint ran clean, resx parity holds with every zero-ref
    key an `Enum_*` built at runtime, and the caching-decorator pass-through tests pin
    non-caching rather than restating.

Findings raised after 3e:

16. **Mine's received driver answers show no trip at all.** The reviewer gate on finding 1(a)
    read the other side of the page: when a driver answers a rider's pin, the rider's Requests
    section renders the answer through the same partial with `IOwnPosting = true`, which
    shows the driver's name, the interest status and the seat count and nothing about the
    trip — not its date, not its place, not its status. A rider can press Accept on an answer
    whose trip was cancelled since and get the `RideUnavailable` toast with no way to have
    seen it coming. Same partial, same existing strings; not struck because the reviewer
    raised it after the strike list was set and it changes what a page shows. Needs Peter.
17. **The Freshness lens proposed freshness triggers on `authorization.md` and
    `data-access.md`** — and `docs/architecture/freshness-catalog.yml` lists exactly those
    files, with `health.md` and the dated records, as markers they must never carry. The
    Backdoor run's retro recorded the same proposal from the same lens a week earlier. Not
    applied. Proposed one-line edit to Phase 3d's Freshness lens: name the catalog's
    never-carry list so the lens stops re-deriving a repo convention as section drift.
18. **Test residue.** Rules the strike did not reach — the express-side capacity, cancelled-trip
    and minimum-seats refusals, accept/decline on a non-pending interest, a request edit by a
    non-owner — recorded in the section's new `Docs/debt.yml` as light-review test gaps.
19. **The Inbox thread cannot run where 3d sends it.** `doctor-reader` has no GitHub tools, so
    a cloud run's Inbox lens self-runs on the main thread through the MCP tools; this run did
    (zero open issues against the section in either repo, zero ledger items). Proposed
    one-line edit to Phase 3d: say that Inbox runs on main in a cloud run rather than leaving
    each run to discover the dispatch cannot reach GitHub.

## Worked

- Target shape — `Docs/health.md` (new), commit `96929857`; finding 12 folded in.
- Finding 10 — commit `79dabf06`.
- Finding 11 — commit `939c7d67`.
- Findings 3, 9 — commit `534ceae2`.
- Finding 2 — commit `7ececdd5`.
- Finding 1(a) — commit `b51c559b`, reviewer-approved.
- Findings 4, 5, 6, 7, 8 — commit `41d9c2f4`.
- Sweep — commit `4ae99151` (Consent, Feedback and Backdoor queues; every item verified still
  standing on `origin/main` and absent from every open doctor branch before it was written).
- Section debt ledger — `Docs/debt.yml` (new): finding 18.

## Skipped + why

- Finding 1(b) — business behavior; Needs Peter.
- Finding 13 — a dated report, not a live doc; Needs Peter whether to touch it at all.
- Finding 14 — retention, nothing to strike.
- Finding 15 — nothing to strike.
- Finding 16 — raised after the strike list was set and changes what a page shows; Needs Peter.
- Findings 17, 19 — lessons about this skill; a run may not edit its files.
- Sections passed over as blocked by an open doctor PR: every section with a
  `section-doctor/*` branch on `origin` at selection time; Rideshare was the one never-doctored
  section not blocked, so the selector had no tier choice to make.

## Retro (Phase 6)

- **Selector/rubric:** the pick was forced — one section in the tier, the rest blocked. The
  rationale line in `selection.txt` says "median of 1", which is honest and a little absurd; a
  tier with nothing to choose between is a pick, not a median. Not worth a rubric change.
- **Wasted motion:** the Freshness lens spent its trigger pass on a proposal the catalog
  forbids (finding 17), for the second run running. The haiku Conformance thread returned a
  disposition list naming paths that do not exist in the section, so `## File coverage`
  below is built from the 3a inventory, not from its dispositions — the same failure the
  Backdoor run recorded for the same model. The Comments and History threads read the same
  citation in `Section.cs` and reached opposite verdicts, which cost a decision on main but
  was the right kind of disagreement to surface.
- **Assessment missed, striking revealed:** finding 16 came out of the reviewer gate, not the
  assessment — the Behavior lens walked the cancel flow from the rider-on-a-trip side and
  never turned the page around to the rider-with-a-pin side. Finding 1's own strike is what put
  the reviewer in front of the partial. The controller-test strike also revealed that a plain
  test project cannot instantiate the section's controller without the shared framework
  reference; the Tests lens named the play and not the prerequisite.
- **Target diff:** first run for this section — no prior target to diff. `Docs/health.md` is
  new, and its first draft was wrong in the ways finding 12 lists; the Freshness thread caught
  the path, the main thread caught the rest on the trace gate.

## Needs Peter

- [ ] 1 — should cancelling an offer withdraw its pending and accepted interests and tell the accepted riders, or stay silent as today?
- [ ] 13 — leave the stale line in `docs/freshness/last-report.md` alone as a dated report, or fix it?
- [ ] 16 — render the driver's trip (direction, place, date, status badge) on the rider's received-answer rows, same partial, no new strings: yes or no?
- [ ] 17 — Phase 3d: name `freshness-catalog.yml`'s never-carry list in the Freshness lens so it stops proposing triggers on `authorization.md` / `data-access.md`?
- [ ] 19 — Phase 3d: state that the Inbox lens runs on the main thread in a cloud run, since `doctor-reader` has no GitHub tools?

## Sweep queue

None — this run's debt items were written directly to their ledgers in this PR (the section's
`Docs/debt.yml`; the sweep commit applied the Consent, Feedback and Backdoor queues to the
central ledger, the Email, GoogleIntegration and Shifts ledgers, and a new memory atom for
the push-by-URL rule; the Calendar item was already fixed on main by the time main was merged
in, and was dropped).

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Rideshare/Controllers/RideshareAdminController.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Controllers/RideshareApiController.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Controllers/RideshareController.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareInterestConfiguration.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareRequestConfiguration.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareSettingsConfiguration.cs` | changed |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareTripConfiguration.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/IRideshareRepository.cs` | changed |
| `src/Sections/Humans.Rideshare/Data/Migrations/20260909233307_InitialRideshare.Designer.cs` | generated |
| `src/Sections/Humans.Rideshare/Data/Migrations/20260909233307_InitialRideshare.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/Migrations/RideshareDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Rideshare/Data/RideshareDbContext.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/RideshareDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Data/RideshareRepository.cs` | changed |
| `src/Sections/Humans.Rideshare/Docs/2026-06-14-rideshare-section-design.md` | reviewed |
| `src/Sections/Humans.Rideshare/Docs/Rideshare.md` | changed |
| `src/Sections/Humans.Rideshare/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Rideshare/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.Rideshare/Docs/debt.yml` | changed (new) |
| `src/Sections/Humans.Rideshare/Docs/features/rideshare-board.md` | changed |
| `src/Sections/Humans.Rideshare/Docs/health.md` | changed (new) |
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
| `src/Sections/Humans.Rideshare/Humans.Rideshare.csproj` | changed |
| `src/Sections/Humans.Rideshare/Models/AdminViewModels.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/BoardFeatureCollection.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/BoardViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/MineViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/OfferFormViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/RequestFormViewModel.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Models/RideshareDates.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.ca.resx` | reviewed |
| `src/Sections/Humans.Rideshare/RideshareResource.cs` | changed |
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
| `src/Sections/Humans.Rideshare/Services/CachingRideshareService.cs` | changed |
| `src/Sections/Humans.Rideshare/Services/IRideshareService.cs` | changed |
| `src/Sections/Humans.Rideshare/Services/RideshareReadModels.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/RideshareRuleException.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/RideshareService.cs` | changed |
| `src/Sections/Humans.Rideshare/Services/Routing/IRouteProvider.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/Routing/OpenRouteServiceClient.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Services/Routing/RouteProviderOptions.cs` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Index.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Mine.cshtml` | changed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Offer.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/RideRequest.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/Rideshare/_InterestRow.cshtml` | changed |
| `src/Sections/Humans.Rideshare/Views/RideshareAdmin/Day.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/RideshareAdmin/Index.cshtml` | changed |
| `src/Sections/Humans.Rideshare/Views/RideshareAdmin/_ViewStart.cshtml` | reviewed |
| `src/Sections/Humans.Rideshare/Views/_ViewImports.cshtml` | changed |
| `src/Sections/Humans.Rideshare/wwwroot/js/rideshare/board.js` | reviewed |
| `src/Sections/Humans.Rideshare/wwwroot/js/rideshare/pick-point.js` | reviewed |
| `tests/Humans.Rideshare.Tests/Controllers/RideshareControllerTests.cs` | changed (new) |
| `tests/Humans.Rideshare.Tests/Humans.Rideshare.Tests.csproj` | changed |
| `tests/Humans.Rideshare.Tests/Infrastructure/RideshareTestHarness.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Models/BoardAndDayViewModelTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Models/BoardFeatureCollectionTests.cs` | changed (new) |
| `tests/Humans.Rideshare.Tests/Models/FormViewModelTests.cs` | changed (new) |
| `tests/Humans.Rideshare.Tests/Models/MineViewModelTests.cs` | changed (new) |
| `tests/Humans.Rideshare.Tests/RideshareArchitectureTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Services/CachingRideshareServiceTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Services/OpenRouteServiceClientTests.cs` | reviewed |
| `tests/Humans.Rideshare.Tests/Services/RideshareServiceTests.cs` | changed |
| `tests/Humans.Rideshare.Tests/Services/RideshareSnapshotTests.cs` | reviewed |

## Threads

| Thread | How | Model | Findings |
|---|---|---|---|
| Shape | main | (main session) | 5 (S-1..S-5; 3 actionable) |
| Behavior & bugs | main | (main session) | 7 (B-1..B-7; 2 actionable) |
| Freshness | subagent (`doctor-reader`) | opus low | 8 (F-1..F-8) |
| Conformance | background + subagent (`claude`) | haiku | 0 |
| Tests | subagent (`doctor-reader`) | opus low | 12 (T-1..T-12) |
| Prose & surface | background + subagent (`claude`) | haiku | 0 |
| History | subagent (`doctor-reader`) | opus low | 13 (H-1..H-13) |
| Comments | subagent (`doctor-reader`) | opus low | 10 (C-1..C-10) |
| Inbox | self-run on main | (main session) | 0 |
| Reviewer gate (not a 3d thread) | subagent (`doctor-reviewer`) | fable high | 1 approve, raising finding 16 |

Every thread ran; none was skipped. Inbox self-ran because `doctor-reader` carries no GitHub
tools (finding 19); the lens itself was complete — both repos' open issues and the ledgers
were read.

The per-lens counts are transcribed from each thread's checkpoint file under
`$RUNDIR/assessment/`, where every finding carries its lens prefix. Caveats: Freshness's
tally includes the trigger proposal that did not survive verification (finding 17), and the
haiku Conformance thread's disposition list named paths outside the section, so `## File
coverage` is from the 3a inventory.
