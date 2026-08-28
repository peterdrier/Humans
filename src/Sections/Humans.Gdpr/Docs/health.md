# Gdpr — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

When a member asks for a copy of everything the organisation holds about them, this is
what assembles it. It asks every part of the system that keeps anything about a person
for its portion, stacks the answers into one dated document, and hands that back as a
file the person downloads. If any part fails to answer, the whole download fails — an
incomplete copy handed over as if it were complete is worse than no copy.

It also sets the terms on which a part of the system may hold personal data at all:
anything that answers the "what do you hold" question must also declare, category by
category, what it deletes when the person asks to be forgotten and what it keeps and
why. Running the forgetting is somebody else's job; publishing the terms is this
section's.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Give me everything held about this person, as one document" | both download routes | `IGdprService.ExportForUserAsync` |
| "Here is my portion for this person" | the orchestrator, of every data-owning section | `IUserDataContributor.ContributeForUserAsync` |
| "What do you keep when this person is forgotten, and why?" | the erasure-coverage gate, and Users' deletion job | `IUserDataContributor.ErasureDeclaration` |
| "Forget this person's portion" | Users' deletion job | `IUserDataContributor.EraseForUserAsync` |
| "What is this portion called in the document?" | every contributor, and the docs | `GdprExportSections` constants |
| "Let me download my own copy" (profileless account) | a person on the Guest dashboard | `GET /Guest/DownloadData` |

The vocabulary carrying answers between them is `UserDataSlice` (one portion, name plus
payload) and `GdprExport` (the timestamped bag of portions).

Everything the section does is one of the shapes above. There is no other, and no shape is
served by more than one member.

## 3. Structure

The shapes imply these and no more:

- **A contracts leaf, as its own project.** It holds the fan-out seam
  (`IUserDataContributor`), the vocabulary (`UserDataSlice`, `GdprExportSections`), and
  the orchestrator's own contract (`IGdprService`, `GdprExport`). A project rather
  than a folder because `Humans.Base` does not merely call this section — it *implements*
  the contract, and a folder inside the section would cycle.
- **One internal orchestrator** — the fan-out loop, behind that contract.
- **One internal controller** — the profileless-account download route, which returns a
  file or a redirect and nothing else.
- **One `Section.cs`** registering exactly the orchestrator.

That is today's layout. This section is at its target structurally; what is off-target is
its prose.

## 4. Invariants

- **Complete or fail.** A contributor that throws is logged and re-thrown. The download
  never succeeds with a category silently missing.
- **Section names are unique across contributors.** A duplicate throws, naming the
  section. Never last-writer-wins.
- **A `null` portion is dropped; an empty collection is not.** `null` means the entity
  does not exist for this person; a collection with no rows must arrive as an empty list
  and survive into the JSON as `[]`.
- **The fan-out is sequential.** A simplicity choice, not a correctness one: the original
  shared-`DbContext` hazard died with `HumansDbContext`. One contributor at a time keeps
  failure attribution and log order plain, and there is nothing to win by changing it.
- **No cross-section database reads.** A contributor reads only its own section's tables;
  another section's data arrives through that section's own contributor.
- **The orchestrator owns no table, repository or `DbContext`.**
- **No route exports another person's data.** Neither download action accepts a user id;
  both resolve the caller's own.
- **Export section names are permanent.** Someone who downloaded last year and downloads
  again must be able to diff the two. Add names, never rename them.
- **Every exported category is declared for erasure.** A contributor's
  `ErasureDeclaration` covers every section name it emits, and must be a static table —
  the coverage gate reads it from an uninitialised instance, so it may not touch instance
  state, the database or the clock.

## 5. Seams

- **Export and erasure are split; the contract is not.** `IUserDataContributor` carries
  both halves, but only the export half is orchestrated here — erasure is fanned out by
  `AccountDeletionService` under Users, driven by `ProcessAccountDeletionsJob`. The
  2026-08-03 frozen inventory assigns both to Gdpr. Unresolved; reserved, not built.
- **The coverage gate is vacuous for a section that never implements the contract at
  all.** Both coverage tests enumerate implementers by reflection, so a new user-scoped
  section whose service simply never implements `IUserDataContributor` leaves nothing to
  enumerate and the suite passes. The only guardrail is prose in `design-rules.md` §8a.

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

## Load-bearing weirdness

- **The contracts leaf is a project, not a folder** — the consumer-in-Base test in its
  strongest form: Base implements the contract.
- **The contract carries the erasure half even though this section never calls it.** That
  is the point: a section cannot export a category without accounting for its deletion in
  the same interface.
- **Almost all of this section's reforge surface score is the fan-out seam.** The score is
  dominated by `AddScoped<IUserDataContributor>` registrations counted once per
  registering section, attributed to Gdpr's contracts leaf. It is the architecture working
  as designed, not surface to burn down — a future run should not chase it.
- **`MailerLiteSubscriber` is an erasure-only section name.** MailerLite owns no
  user-scoped table, so it never emits the key and it never appears in an export; the
  constant exists so the section's erasure account has a name to declare.
- **The orchestrator is sequential on purpose,** and looks like an obvious
  parallelisation win. It is not — but note the *reason* has changed and the docs lagged
  it by two moves: it is no longer unsafe to parallelise, merely pointless. A future run
  finding a doc that still calls it a thread-safety requirement is finding drift, not a
  rule.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-27 | Prose across the section describes a layout two moves stale — a deleted project, a deleted `DbContext`, a controller the section has | peterdrier/Humans#1540 |
