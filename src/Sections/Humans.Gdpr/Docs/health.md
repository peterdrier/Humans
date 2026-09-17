# Gdpr — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

When a member asks for a copy of everything the organisation holds about them, this is
what assembles it. It asks every part of the system that keeps anything about a person
for its portion, stacks the answers into one dated document, and hands that back as a
file the person downloads. If any part fails to answer, the whole download fails — an
incomplete copy handed over as if it were complete is worse than no copy.

It runs the mirror of that when the person asks to be forgotten: the same roll-call, in
turn, each part erasing its own portion, the part that holds the identity itself going
last so the others can still reach outside services in the person's name while they work.
It does not decide *when* someone is forgotten — the grace period, the hold on a ticket
and the confirmation afterwards belong elsewhere. It is the roll-call for both halves.

And it sets the terms on which a part of the system may hold personal data at all:
anything that answers the "what do you hold" question must also declare, category by
category, what it deletes when the person asks to be forgotten and what it keeps and why.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Give me everything held about this person, as one document" | both download routes | `IGdprService.ExportForUserAsync` |
| "Forget this person, across every part of the system" | Users' deletion lifecycle, once per account id | `IGdprService.EraseForUserAsync` |
| "Here is my portion for this person" | the orchestrator, of every data-owning section | `IUserDataContributor.ContributeForUserAsync` |
| "Forget my portion of this person" | the orchestrator, of every data-owning section | `IUserDataContributor.EraseForUserAsync` |
| "What do you keep when this person is forgotten, and why?" | the erasure-coverage gate, and the orchestrator's own ordering | `IUserDataContributor.ErasureDeclaration` |
| "What is this portion called in the document?" | every contributor, and the docs | `GdprExportSections` constants |
| "Let me download my own copy" (profileless account) | a person on the Guest dashboard | `GET /Guest/DownloadData` |

The vocabulary carrying answers between them is `UserDataSlice` (one portion, name plus
payload) and `GdprExport` (the timestamped bag of portions).

Everything the section does is one of the shapes above. There is no other, and no shape is
served by more than one member.

## 3. Structure

The shapes imply these and no more:

- **A contracts leaf, as its own project.** It holds the two fan-out seams
  (`IUserDataContributor`), the vocabulary (`UserDataSlice`, `GdprExportSections`), and
  the orchestrator's own contract (`IGdprService`, `GdprExport`). A project rather than a
  folder because the contract is implemented from *outside*: every section owning
  user-scoped tables implements it, so a folder inside `Humans.Gdpr` would make all of
  them reference this section — its internal orchestrator and its controller included —
  to reach an interface. The leaf keeps the implementation invisible to its implementers.
  This section is the rule's own worked example:
  [`docs/sections/G5-SECTION-TEMPLATE.md`](../../../../docs/sections/G5-SECTION-TEMPLATE.md)
  — read consumer-first the contract looks internal; read implementer-first it is obviously
  public, and the implementer count is what decides, not the consumer count.
- **One internal orchestrator** — the two fan-out loops, behind that contract.
- **One internal controller** — the profileless-account download route, which returns a
  file or a redirect and nothing else.
- **One `Section.cs`** registering exactly the orchestrator.

That is today's layout. This section is at its target structurally; what is off-target is
its prose.

## 4. Invariants

- **Complete or fail.** A contributor that throws is logged and re-thrown, in both halves.
  The download never succeeds with a category silently missing, and erasure never reports
  done with a section left behind:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:36` and `:93`.
- **Section names are unique across contributors.** A duplicate throws, naming the
  section. Never last-writer-wins: `src/Sections/Humans.Gdpr/Services/GdprService.cs:57`,
  with the declaration-level counterpart at
  `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:94`.
- **A `null` portion is dropped; an empty collection is not.** `null` means the entity
  does not exist for this person; a collection with no rows must arrive as an empty list
  and survive into the JSON as `[]`:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:48`, pinned at
  `tests/Humans.Gdpr.Tests/Services/GdprServiceTests.cs:148`.
- **Erasure runs the identity holder last, and the order comes from the declarations.**
  The contributor whose `ErasureDeclaration` carries `GdprExportSections.Account` sorts
  last, never a pinned type list: `src/Sections/Humans.Gdpr/Services/GdprService.cs:82`,
  pinned at `tests/Humans.Gdpr.Tests/Services/GdprServiceTests.cs:208`.
- **The fan-out is sequential.** A simplicity choice, not a correctness one: the original
  shared-`DbContext` hazard died with `HumansDbContext`. One contributor at a time keeps
  failure attribution and log order plain, and there is nothing to win by changing it:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:26`, pinned at
  `tests/Humans.Gdpr.Tests/Services/GdprServiceTests.cs:173`.
- **`ExportedAt` is an invariant ISO-8601 UTC instant off the injected clock**, never
  `DateTime.UtcNow`: `src/Sections/Humans.Gdpr/Services/GdprService.cs:71`, pinned at
  `tests/Humans.Gdpr.Tests/Services/GdprServiceTests.cs:21`.
- **The orchestrator owns no table, repository or `DbContext`.** Its whole dependency set
  is the contributor roster, a clock and a logger:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:17`.
- **No route exports another person's data.** Neither download action accepts a user id;
  both resolve the caller's own: `src/Sections/Humans.Gdpr/Controllers/GuestDataController.cs:36`
  and `src/Sections/Humans.Users/Controllers/ProfileController.cs:878`.
- **Every exported category is declared for erasure**, and the declaration must be a
  static table — the coverage gate reads it from an uninitialised instance, so it may not
  touch instance state, the database or the clock:
  `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:81` over the
  instances built at `:67`.

## 5. Seams

- **Export does not follow a merge chain; erasure does.** `EraseForUserAsync` takes one id
  and its contract says the caller loops the chain
  (`src/Sections/Humans.Gdpr.Contracts/IGdprService.cs:30`), which
  `src/Sections/Humans.Users/Services/AccountDeletionService.cs:217` does.
  `ExportForUserAsync` has no such clause and neither caller loops, so each contributor
  remembers the chain for itself or forgets it. Reserved, not built: peterdrier/Humans#1704
  would remove the question by resolving chains inside Users.
- **The coverage gate is vacuous for a section that never implements the contract at
  all.** Both coverage tests enumerate implementers by reflection
  (`tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:67`), so a new
  user-scoped section whose service simply never implements `IUserDataContributor` leaves
  nothing to enumerate and the suite passes. The only guardrail is prose in
  `docs/architecture/design-rules.md`. Tracked as nobodies-collective/Humans#1116.

## 6. Deliberately not done

- **No caching decorator.** An export is a one-off download of live personal data;
  caching it is a privacy hazard, not a performance win.
- **No repository, `DbContext` or owned table.** The moment the orchestrator reads a
  table it is duplicating a contributor and the no-cross-section-reads invariant is gone.
- **No `Resources/` folder or `GdprResource`.** The section renders no page copy.
- **No admin route that exports someone else's data.** Admin and Board get nothing extra
  here.
- **Gdpr does not register the contributor forwarding factories.** Each
  `AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<X>())` belongs beside the
  service that owns `X`; registering them here would make this section name every other
  section's internal service type.
- **`/Profile/Me/DownloadData` is not moved here.** Moving it would change a URL.
- **The deletion lifecycle is not here.** The grace period, the ticket hold, the audit
  entry and the confirmation email belong to `AccountDeletionService` under Users. This
  section owns the roll-call, not the decision to run it.

## Load-bearing weirdness

- **The contracts leaf is a project, not a folder** — because the contract is implemented
  from outside, by every section that owns user-scoped tables. A folder would make all of
  them reference `Humans.Gdpr` itself to reach an interface.
- **One contract carries both halves.** `IUserDataContributor` declares the export slice,
  the erasure and the retention declaration together. That is the point: a section cannot
  export a category without accounting for its deletion in the same interface.
- **Almost all of this section's reforge surface score is the fan-out seam.** The score is
  dominated by `AddScoped<IUserDataContributor>` registrations counted once per
  registering section, attributed to Gdpr's contracts leaf. It is the architecture working
  as designed, not surface to burn down — a future run should not chase it.
- **`MailerLiteSubscriber` is an erasure-only section name.** MailerLite owns no
  user-scoped table, so it never emits the key and it never appears in an export; the
  constant exists so the section's erasure account has a name to declare.
- **The orchestrator is sequential on purpose,** and looks like an obvious
  parallelisation win. It is not — but note the *reason* has changed: it is no longer
  unsafe to parallelise, merely pointless. A doc that still calls it a thread-safety
  requirement is finding drift, not a rule.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-27 | Prose across the section describes a layout two moves stale — a deleted project, a deleted `DbContext`, a controller the section has | peterdrier/Humans#1540 |
| 2 | 2026-09-17 | The section's own rationale for its shape was false, and the target shape had not caught up with erasure moving in | peterdrier/Humans#1723 |
