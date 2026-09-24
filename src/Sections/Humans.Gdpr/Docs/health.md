# Gdpr — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

When a member asks for a copy of everything the organisation holds about them, this is
what assembles it. It asks every part of the system that keeps anything about a person
for its portion, stacks the answers into one dated document that names whose account it
is — and, for an account that absorbed older duplicate accounts, which older ids now
belong to it — and hands that back as a file the person downloads. If any part fails to
answer, the whole download fails: an incomplete copy handed over as if it were complete
is worse than no copy.

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
| "Whose account is this document for, and which older ids fold into it?" | the export itself, once per download | one `IUserServiceRead.GetUserInfoAsync` read, carried on `GdprExport` |
| "Forget this person, across every part of the system" | Users' deletion lifecycle, once per account id | `IGdprService.EraseForUserAsync` |
| "Here is my portion for this person" | the orchestrator, of every data-owning section | `IUserDataContributor.ContributeForUserAsync` |
| "Forget my portion of this person" | the orchestrator, of every data-owning section | `IUserDataContributor.EraseForUserAsync` |
| "What do you keep when this person is forgotten, and why?" | the erasure-coverage gate, and the export's own check | `IUserDataContributor.ErasureDeclaration` |
| "Which of you holds the identity itself?" | the erasure ordering | `IUserDataContributor.ErasesLast` |
| "Let me download my own copy" (profileless account) | a person on the Guest dashboard | `GET /Guest/DownloadData` |

The vocabulary carrying answers between them is `UserDataSlice` (one portion, name plus
payload) and `GdprExport` (the timestamped, account-stamped bag of portions). Each
contributor names its own portions with constants on itself; there is no central list.

## 3. Structure

The shapes imply these and no more:

- **A contracts leaf, as its own project.** It holds the fan-out seam
  (`IUserDataContributor`), the vocabulary (`UserDataSlice`), and the orchestrator's own
  contract (`IGdprService`, `GdprExport`). A project rather than a folder because the
  contract is implemented from *outside*: every section owning user-scoped tables
  implements it, so a folder inside `Humans.Gdpr` would make all of them reference this
  section to reach an interface.
- **One internal orchestrator** — the two fan-out loops plus the one account read the
  envelope needs, behind that contract.
- **One internal controller** — the profileless-account download route, which returns a
  file or a redirect and nothing else.
- **One `Section.cs`** registering exactly the orchestrator.

That is today's layout. Off-target: the document's serialisation (key order, JSON options)
is written twice, once per download route, in two sections — tracked in the central
ledger, a fix that adds public surface either way.

## 4. Invariants

- **Complete or fail.** A contributor that throws is logged and re-thrown, in both halves:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:46` (export) and
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:109` (erasure).
- **Portion names are unique in one document.** A duplicate throws, naming the portion —
  never last-writer-wins: `src/Sections/Humans.Gdpr/Services/GdprService.cs:70`. The
  declaration-level counterpart, across every registered contributor, is
  `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:64`.
- **An exported portion with no erasure declaration is logged, not dropped.** The
  export still carries it — it is still the person's data — and an error names the portion
  and the contributor: `src/Sections/Humans.Gdpr/Services/GdprService.cs:51`.
- **A `null` portion is dropped; an empty collection is not:**
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:59`.
- **The envelope names the surviving account.** A download asked under a merged-away id
  is stamped with the survivor's id and the archived ids; an unknown id falls back to the
  id asked with: `src/Sections/Humans.Gdpr/Services/GdprService.cs:85`.
- **Erasure runs the identity holder last, and the order comes from the declarations**,
  never a pinned type list: `src/Sections/Humans.Gdpr/Services/GdprService.cs:95`. Exactly
  one contributor claims it: `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:89`.
- **The fan-out is sequential.** A simplicity choice, not a correctness one; one
  contributor at a time keeps failure attribution and log order plain:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:36`.
- **`ExportedAt` is an invariant ISO-8601 UTC instant off the injected clock**:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:84`.
- **No route exports another person's data.** Neither download action accepts a user id;
  both resolve the caller's own: `src/Sections/Humans.Gdpr/Controllers/GuestDataController.cs:38`
  and `src/Sections/Humans.Users/Controllers/ProfileController.cs:881`.
- **Every declared category is either erased in full or states a lawful basis** of real
  length: `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:103`, over
  instances built uninitialised at
  `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:50` — so a declaration
  may not touch instance state, the database or the clock.

## 5. Seams

- **Export does not follow a merge chain for its contributors; erasure does.**
  `EraseForUserAsync` takes one id and the caller loops the chain
  (`src/Sections/Humans.Users/Services/AccountDeletionService.cs:213`). The export now
  *names* the chain on its envelope but still passes contributors only the id asked with,
  so each contributor remembers the chain for itself or forgets it. Reserved, not built:
  peterdrier/Humans#1704 would remove the question by resolving chains inside Users.
- **The coverage gate is vacuous for a section that never implements the contract at
  all.** The coverage tests enumerate implementers by reflection, so a new user-scoped
  section whose service never implements `IUserDataContributor` leaves nothing to
  enumerate. The only guardrail is prose in `docs/architecture/design-rules.md`. Tracked as
  CENTRAL-67 in `docs/architecture/debt-ledger.yml`; nobodies-collective/Humans#1116, which described it,
  is closed.

## 6. Deliberately not done

- **No caching decorator.** An export is a one-off download of live personal data;
  caching it is a privacy hazard, not a performance win.
- **No repository, `DbContext` or owned table.** The moment the orchestrator reads a
  table it is duplicating a contributor.
- **No `Resources/` folder or `GdprResource`.** The section renders no page copy of its own;
  its one error toast uses the shared `Error_TryAgainLater` key.
- **No admin route that exports someone else's data.**
- **Gdpr does not register the contributor forwarding factories.** Each belongs beside the
  service that owns it; registering them here would make this section name every other
  section's internal service type.
- **`/Profile/Me/DownloadData` is not moved here.** Moving it would change a URL.
- **The deletion lifecycle is not here.** Grace period, ticket hold, audit entry and
  confirmation email belong to `AccountDeletionService` under Users.
- **The export does not refuse an undeclared portion.** Dropping it would hand the person
  an incomplete copy; the erasure gap is an operator's problem, surfaced by the log line.

## Load-bearing weirdness

- **The contracts leaf is a project, not a folder** — the contract is implemented from
  outside, by every section that owns user-scoped tables.
- **One contract carries both halves.** `IUserDataContributor` declares the export portion,
  the erasure and the retention declaration together, so a section cannot export a
  category without accounting for its deletion in the same interface.
- **The one outbound dependency is `IUserServiceRead`.** Everything else the orchestrator
  touches is its own leaf; the account read exists only to stamp the envelope with the
  surviving id and the archived ids, and contributors still resolve for themselves.
- **Almost all of this section's reforge surface score is the fan-out seam** — the
  `AddScoped<IUserDataContributor>` registrations each data-owning section makes. The
  architecture working as designed, not surface to burn down.
- **`MailerLiteSubscriber` is an erasure-only portion name.** MailerLite owns no
  user-scoped table, so it never exports the key; the constant exists so its erasure
  account has a name to declare.
- **The orchestrator is sequential on purpose,** and looks like an obvious
  parallelisation win. Parallelising it is safe but buys nothing at this scale.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-27 | Prose across the section describes a layout two moves stale — a deleted project, a deleted `DbContext`, a controller the section has | peterdrier/Humans#1540 |
| 2 | 2026-09-17 | The section's own rationale for its shape was false, and the target shape had not caught up with erasure moving in | peterdrier/Humans#1723 |
| 3 | 2026-09-23 | The envelope learned whose account it is and the prose did not; a "logged" check was documented as "enforced" | peterdrier/Humans#1811 |
