# section-doctor — Workgroups — 2026-09-16

- Invocation: unattended daily run (cloud), no arguments; Phase 8 skipped per the stored prompt
- Anchor commit: `f1298b228` (origin/main at branch point); branch `section-doctor/2026-09-16T011651Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1710

## Assessment summary
Workgroups is the register of association-level working groups and the record of what each
one did. It is the newest section in the repo — a single commit, shipped six days before this
run — and it reads like it: the layers are clean, the repository is the only thing that touches
the tables, the caching decorator holds the whole register under one key and clears it in a
`finally`, and the reporting rhythm lives in pure functions so the daily job and the page badges
answer from the same code. Its reforge score is the register-sized service class and the
crosscuts it calls, not disorder.

What was wrong was the prose. The invariant doc told a reader the section had no test project
and instructed the next agent to create one; the project exists and passes. The same doc denied
a Surveys dependency it documents in its own data model. Comments described the slug column as
unique when the index is deliberately plain, and described the member log kinds as three when
the service admits four — the fourth being the one that lets a hand-made post skip the status
cooldown. Everything struck this run is that: prose a reader would act on and be wrong.

The section's real gaps are behavioural and belong to Peter: an edit path that takes an actor
and records nothing, a log kind reachable outside the flow that governs it, and a clause-5
obligation the code computes and nobody acts on. None is a defect in what shipped; each is a
decision nobody has made yet.

## Findings
Independence check: pass. The main thread derived the controller and service findings from the
code before any dispatched thread returned; the threads produced the doc and comment findings
from their own lenses. Where two threads landed on the same line — Freshness and History on the
test-project claim, Comments and History on the settled-decision prose — each cited the code
rather than the other. Nothing here rests on `Docs/health.md` alone: every item health.md also
states was re-derived against a `file:line` by the thread that raised it.

| Rank | Finding | Where | Thread |
|---|---|---|---|
| 1 | The invariant doc said `tests/Humans.Workgroups.Tests` does not exist and told the next agent to create it. It exists and is green. | `Docs/Workgroups.md` | Freshness, History |
| 2 | The doc denied a direct Surveys dependency the same doc states in its data model. `WorkgroupService` injects `ISurveyAnalysisRead`. | `Docs/Workgroups.md` | Freshness |
| 3 | Both cross-section call lists omitted `ISurveyAnalysisRead`. | `Docs/Workgroups.md`, `Docs/data-access.md` | Freshness |
| 4 | `Slug`'s doc comment called the column unique; the index is plain, and the configuration says so on the line above it. | `Domain/Workgroup.cs` | Comments |
| 5 | The log-entry DTO and the service interface described member kinds as Update, Disclosure or Note; `RequireMemberKind` admits `StatusRequested` too. | `Services/Dtos/WorkgroupDtos.cs`, `Services/IWorkgroupService.cs` | Comments |
| 6 | The doc's index lines omitted the filtered attribution index on the child tables, and the log-entry table had no index line at all. | `Docs/Workgroups.md` | Freshness |
| 7 | Comments that restate the line under them or argue a settled call: a seam that was never built, a repeated localization-exempt sentence, a DI registration, a meeting flag, a set of view-name constants. | `SectionChrome.cs`, `Controllers/WorkgroupsAdminController.cs`, `Section.cs`, `Domain/WorkgroupMeeting.cs`, `Controllers/WorkgroupsController.cs` | Comments, History |
| 8 | The coordinator-is-not-a-permission rule was framed as a v1 contrast with a future nobody has planned. | `Domain/Enums.cs`, `Authorization/WorkgroupOperationRequirement.cs`, `Docs/Workgroups.md` | History |
| 9 | `Edit` (GET) and `MemberFormAsync` bound the current user and never used it, where `Apply` (GET) already discards it. | `Controllers/WorkgroupsController.cs` | Shape |
| 10 | `OneGroupsFailure_DoesNotStopThePassForTheRest` made nothing fail, so the rhythm pass's per-group swallow was unpinned. Two more names claimed more than their bodies, a redundant fact pair had a theory waiting for it, and one deny-path test asserted less than its siblings. | `tests/Humans.Workgroups.Tests` | Tests |

Reported, not struck — see **Needs Peter** for the behavioural ones:

- `WorkgroupLogKind.SurveySent` is declared and never written; the send half of the survey lane
  is specified and unbuilt.
- `WorkgroupOperationRequirement.Read` has no production caller — the register is gated at the
  controller instead.
- Invariants left unpinned by tests: the dormancy flag's write ordering, `CloseCommentsAsync`
  entirely, log-entry immutability, the reserved-slug collision loop, the hide-comment audit, and
  `ApplyAsync`'s token overwrite. No test file targets `WorkgroupsAdminController`.
- Workgroups has no row in `docs/architecture/freshness-catalog.yml` or
  `docs/architecture/dependency-graph.md`.

Conformance and Prose & surface both came back clean: the section's layout, resource-key
prefixes, translations in every supported culture, dead-key sweep, navigation and view-model
members all check out.

## Worked
Each item is one commit.

- `doctor(workgroups): the invariant doc said the test project does not exist` — items one, two,
  three and six above, plus the test tree added to `freshness:triggers`, which is why the
  test-project claim aged unseen.
- `doctor(workgroups): comments that disagreed with the code` — items four and five.
- `doctor(workgroups): prose that restated the line under it or narrated a settled call` — items
  seven and eight. `Docs/health.md` landed in this commit rather than its own; see **Retro**.
- `doctor(workgroups): two helpers bound the current user and never used it` — item nine.
- `doctor(workgroups): tests whose names claimed more than their bodies checked` — item ten. The
  rhythm-isolation test now makes the first group's audit throw; removing the service's catch
  turns it red, which was checked before the commit.
- `doctor(workgroups): health.md gains this run's history row and two seams`.

## Skipped
| Item | Why |
|---|---|
| `Services/WorkgroupService.cs` "Peter 2026-09-14: author == actor is the whole check." | Peter's attributed instruction to a future reviewer. The name and the date are what give it force; stripping them leaves an assertion anyone may argue with. |
| `Docs/debt.yml`'s emptied-inbox note | Provenance for a deliberately empty ledger. Both threads that raised it said low confidence, and an empty ledger with no note reads as an oversight. |
| `Docs/2026-09-10-workgroups-section-design.md`'s migration path | A dated design record's forward-looking plan, not a false current claim. The record is six days old and fails the deletion age gate anyway. |
| The mock-shaped assertion in `WorkgroupServiceRegistrationTests.cs`, and `Services/WorkgroupService.Helpers.cs`'s notification recipient resolution | Both are in flight on open PR peterdrier/Humans#1706. Marked do-not-strike at selection. |
| `ActAsync` and `FormAsync`'s duplicate `catch` clauses | The clauses share only their logging; the tails differ — a redirect for a button, a re-render for a form. Collapsing them buys a helper and costs the reader. |
| `WorkgroupRhythm.UpcomingMeetings` having one caller | A named seam with a load-bearing comment. One caller is not dead. |

## Retro
The selector spent about ten minutes inside `reforge surface-score` over the whole solution,
which is longer than a Bash call is allowed to block. Backgrounding it and waiting on the output
file worked; a future run should background it from the start rather than discovering the
timeout.

`git grep -c -w "$s" -- <paths> -A 20` fails: `git grep` reads `-A` as a revision. Plain
`grep -rn -A 20` is the substitute.

This container has no `gh`. Every GitHub read went through the MCP tools on the main thread,
which is also why the Inbox thread ran on main rather than as a dispatched reader — a
`doctor-reader` subagent here cannot reach them. Before recording either backlog as clean, a
held issue in each repository was read to prove reach.

`Docs/health.md` was swept into the prose-cut commit by a `git add -A` over the section
directory instead of being committed on its own. Nothing is wrong in the tree, but the commit
message does not mention the file it added. Stage by path.

Three of the `file:line` cites first written into `Docs/health.md` pointed at a comment banner
or the neighbouring method rather than the line claimed. They were caught by opening every cited
line rather than by the trace gate, which only checks that the file exists and is long enough.
Cites are worth re-reading one by one.

## Needs Peter
1. **A member can plant a status request that skips the cooldown.** The log form offers Update,
   Disclosure and Note, but `Kind` is model-bound and `RequireMemberKind` admits
   `StatusRequested`, so a hand-made post writes one directly — bypassing
   `RequestStatusAsync`'s per-person seven-day cooldown and the coordinator notification, and
   driving the overdue badge and the things-to-do entry. Gated to members of an Active group, so
   the blast is small. Narrow `RequireMemberKind` for the write path, or accept the two doors?
2. **Three service methods take an actor they never spend.** `UpdateMeetingAsync`,
   `DeleteMeetingAsync` and `UpdateLogEntryAsync` accept `actorUserId` and ignore it, while
   `DeleteLogEntryAsync` takes the same parameter and audits with it. Either the edit paths owe
   an audit entry or the parameter is surplus. Both are one-line fixes in opposite directions,
   so neither was made.
3. **Clause 5's annual report is computed and nobody acts on it.** `IsAnnualReportDue` reaches
   the group page as a badge; the daily pass does not read it, no notice goes out, and the Board
   gets no queue row. The obligation is real and the surface is missing.
4. **Does this section owe an `Architecture/` test folder?** The test project exists and has
   none. `no-tests-for-absences` says the run may not assert one is owed, so the question is
   yours. The doc's old claim that the project did not exist is now corrected either way.
5. **Workgroups is absent from the repo-wide ledgers.** No row in
   `docs/architecture/freshness-catalog.yml` and none in `docs/architecture/dependency-graph.md`.
   Outside this section's lane, so nothing was added.

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Workgroups/Authorization/WorkgroupAuthorizationHandler.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Authorization/WorkgroupOperationRequirement.cs` | changed |
| `src/Sections/Humans.Workgroups/Controllers/WorkgroupsAdminController.cs` | changed |
| `src/Sections/Humans.Workgroups/Controllers/WorkgroupsController.cs` | changed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupDocumentCommentConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupDocumentConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupLogEntryConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupMeetingConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupMemberConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupsJson.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/IWorkgroupRepository.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260911012653_AddWorkgroups.Designer.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260911012653_AddWorkgroups.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Migrations/WorkgroupsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/WorkgroupRepository.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/WorkgroupsDbContext.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/WorkgroupsDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Docs/2026-09-10-workgroups-section-design.md` | reviewed |
| `src/Sections/Humans.Workgroups/Docs/Workgroups.md` | changed |
| `src/Sections/Humans.Workgroups/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Workgroups/Docs/data-access.md` | changed |
| `src/Sections/Humans.Workgroups/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.Workgroups/Docs/health.md` | changed |
| `src/Sections/Humans.Workgroups/Domain/Enums.cs` | changed |
| `src/Sections/Humans.Workgroups/Domain/Workgroup.cs` | changed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupDocument.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupDocumentComment.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupLogEntry.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupMeeting.cs` | changed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupMember.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Humans.Workgroups.csproj` | reviewed |
| `src/Sections/Humans.Workgroups/Jobs/WorkgroupRhythmJob.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Models/WorkgroupViewModels.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Section.cs` | changed |
| `src/Sections/Humans.Workgroups/SectionAdminTiles.cs` | reviewed |
| `src/Sections/Humans.Workgroups/SectionChrome.cs` | changed |
| `src/Sections/Humans.Workgroups/SectionJobs.cs` | reviewed |
| `src/Sections/Humans.Workgroups/SectionMemberDashboard.cs` | reviewed |
| `src/Sections/Humans.Workgroups/SectionThingsToDo.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/CachingWorkgroupService.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/Contributors/WorkgroupCalendarContributor.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/Contributors/WorkgroupDriveAccessSource.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/Dtos/WorkgroupDtos.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/IWorkgroupService.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupErrorKeys.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupRhythm.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupRuleException.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Documents.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Gdpr.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Helpers.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Lifecycle.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Rhythm.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.cs` | reviewed |
| `src/Sections/Humans.Workgroups/ViewComponents/GovernanceWorkgroupsViewComponent.cs` | reviewed |
| `src/Sections/Humans.Workgroups/ViewComponents/MyWorkgroupsViewComponent.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Shared/Components/GovernanceWorkgroups/Default.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Shared/Components/MyWorkgroups/Default.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Apply.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Details.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Document.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/DocumentForm.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Edit.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Index.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/LogEntryForm.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/MeetingForm.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/Index.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/RegisterExisting.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/Settings.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/_ViewStart.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/_ViewImports.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.ca.resx` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.cs` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.de.resx` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.es.resx` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.fr.resx` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.it.resx` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.resx` | reviewed |
| `tests/Humans.Workgroups.Tests/Authorization/WorkgroupAuthorizationHandlerTests.cs` | changed |
| `tests/Humans.Workgroups.Tests/Controllers/WorkgroupsControllerAuthorizationTests.cs` | changed |
| `tests/Humans.Workgroups.Tests/Humans.Workgroups.Tests.csproj` | reviewed |
| `tests/Humans.Workgroups.Tests/Infrastructure/WorkgroupsTestHarness.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/Contributors/WorkgroupCalendarContributorTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/Contributors/WorkgroupDriveAccessSourceTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceDocumentTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceGdprTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceLifecycleTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceMembershipTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceRegistrationTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceRhythmTests.cs` | changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | opus (main thread) | Two helpers binding an unused user; the duplicate error map, judged not worth collapsing; `Read` with no production caller; `UpcomingMeetings` with one caller |
| Behavior & bugs | main | opus (main thread) | The `StatusRequested` second door; three methods taking an actor they never spend; `IsAnnualReportDue` computed and unread — all three reported, not struck |
| Freshness | subagent (`doctor-reader`) | opus-low | The test-project claim, the denied Surveys dependency, both call lists, the index lines; the design record's migration path and the missing ledger rows |
| Conformance | subagent (`doctor-reader`) | haiku | clean |
| Tests | subagent (`doctor-reader`) | opus-low | Two names claiming more than their bodies, a redundant pair, a weak deny-path sibling, and the coverage matrices behind the unpinned list |
| Prose & surface | subagent (`doctor-reader`) | haiku | clean |
| History | subagent (`doctor-reader`) | opus-low | The test-project claim, the settled-seam remarks, the dated attribution, the emptied-ledger note, the v1 framing |
| Comments | subagent (`doctor-reader`) | opus-low | The slug uniqueness claim, the member-kind claim, and the comments restating the line under them |
| Inbox | main (self-run) | opus (main thread) | clean — no open issue names this section in either repository; reach proved by reading a held issue in each first |

