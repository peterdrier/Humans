# Gdpr — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

When a member asks for a copy of everything the organisation holds about them, this is
what assembles it. It asks every part of the system that keeps anything about a person
for its portion, stacks the answers into one dated document that names whose account it
is — and, for an account that absorbed older duplicate accounts, which older ids now
belong to it — and hands that back as a file the person downloads. If any part fails to
answer, the whole download fails.

It runs the mirror of that when the person is forgotten: the same roll-call, each part
erasing its own portion, the part that holds the identity itself going last. It does not
decide *when* someone is forgotten — the grace period, the hold on a ticket and the
confirmation email belong to Users.

It is also the person's own door to both rights: the download buttons, the "delete my
account" request and its cancel, the page a person lands on while their deletion is
pending, and the wall a suspended, rejected, deleted or merged account sees instead of the
app. None of those pages decide anything; they hand the request to the export or to Users'
deletion lifecycle and say what happened.

And it sets the terms on which a part of the system may hold personal data at all:
anything that answers the "what do you hold" question must also declare, category by
category, what it deletes when the person is forgotten and what it keeps and why.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Give me everything held about this person, as one document" | both download routes | `IGdprService.ExportForUserAsync` |
| "Write that document as the file I download" | both download routes | `GdprExportSerializer.Serialize` |
| "Forget this person, across every part of the system" | Users' deletion lifecycle, once per account id | `IGdprService.EraseForUserAsync` |
| "Here is my portion for this person" | the orchestrator, of every data-owning section | `IUserDataContributor.ContributeForUserAsync` |
| "Here is my portion, given the whole merge chain" | the orchestrator, of a contributor that cannot resolve the chain itself | `IUserDataContributor.ContributeForIdentityChainAsync` |
| "Forget my portion of this person" | the orchestrator, of every data-owning section | `IUserDataContributor.EraseForUserAsync` |
| "What do you keep when this person is forgotten, and why?" | the erasure-coverage gate, and the export's own check | `IUserDataContributor.ErasureDeclaration` |
| "Which of you holds the identity itself?" | the erasure ordering | `IUserDataContributor.ErasesLast` |
| "Let me download my own copy" | a member on `/Consent/MyData`; a profileless account on `/Guest` | `GET /Gdpr/DownloadData`, `GET /Gdpr/Guest/DownloadData` |
| "Start, or call off, the deletion of my account" | the same two pages; the pending-deletion landing | `POST /Gdpr/RequestDeletion`, `POST /Gdpr/Guest/RequestDeletion`, `POST /Gdpr/Guest/CancelDeletion`, `POST /Gdpr/Deletion/Cancel` |
| "Why can't I get in?" | `MembershipRequiredFilter`'s redirects | `GET /Gdpr/Status`, `GET /Gdpr/Deletion` |

The vocabulary carrying answers between them is `UserDataSlice` (one portion, name plus
payload) and `GdprExport` (the timestamped, account-stamped bag of portions). Each
contributor names its own portions with constants on itself; there is no central list.

## 3. Structure

The shapes imply these and no more:

- **A `Contracts/` folder** holding the fan-out seam (`IUserDataContributor`), the
  vocabulary (`UserDataSlice`, `GdprExport`), the orchestrator's contract (`IGdprService`)
  and the one file format (`GdprExportSerializer`). Every section owning user-scoped tables
  references `Humans.Gdpr` to implement the seam.
- **One internal orchestrator** — the two fan-out loops plus the one account read the
  envelope needs.
- **Internal controllers that translate only** — the member pair (download, request
  deletion), the profileless-account trio (download, request, cancel), and the two landings
  the membership filter redirects to (status wall, pending deletion with its cancel). The
  deletion work is Users' `IAccountDeletionService`; the controllers map its result to a
  flash and a redirect.
- **Two views and one resource set** (`GdprResource`, six cultures) for the landings and
  the flashes.
- **One `Section.cs`** registering exactly the orchestrator.

Off-target: the member and profileless download actions are one flow written twice, differing
in how a missing user is answered and the redirect back; and the two
cancel-deletion actions map the same failure differently (finding-level, see the run file).

## 4. Invariants

- **Complete or fail.** A contributor that throws is logged and re-thrown, in both halves:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:50` (export) and
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:111` (erasure).
- **Portion names are unique in one document**, across contributors and across a
  contributor's two calls. A duplicate throws, naming the portion:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:74`. The declaration-level counterpart,
  across every registered contributor, is
  `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:44`.
- **An exported portion with no erasure declaration is logged, not dropped**:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:57`.
- **A `null` portion is dropped; an empty collection is not:**
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:65`.
- **Contributors are asked under the surviving account.** The id asked with is resolved once;
  every contributor, the chain-aware call and the envelope get the survivor, and an unknown id
  falls back to the id asked with: `src/Sections/Humans.Gdpr/Services/GdprService.cs:18`.
- **Erasure runs the identity holder last, and the order comes from the declarations**,
  never a pinned type list: `src/Sections/Humans.Gdpr/Services/GdprService.cs:97`. Exactly
  one contributor claims it: `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:69`.
- **The fan-out is sequential**: `src/Sections/Humans.Gdpr/Services/GdprService.cs:23`.
- **`ExportedAt` is an ISO-8601 UTC instant off the injected clock**:
  `src/Sections/Humans.Gdpr/Services/GdprService.cs:88`.
- **The file is the envelope first, then the portions in contributor order:**
  `src/Sections/Humans.Gdpr/Contracts/GdprExportSerializer.cs:19`.
- **No route acts on another person.** No action takes a user id; each resolves the caller:
  `src/Sections/Humans.Gdpr/Controllers/GuestDataController.cs:29`,
  `src/Sections/Humans.Gdpr/Controllers/MemberDataController.cs:28`,
  `src/Sections/Humans.Gdpr/Controllers/UserController.cs:64`.
- **A request-aborted download propagates the cancellation** rather than redirecting with a
  failure flash: `src/Sections/Humans.Gdpr/Controllers/GuestDataController.cs:42` and
  `src/Sections/Humans.Gdpr/Controllers/MemberDataController.cs:63`.
- **The status wall shows only to a walled account;** anyone else is sent home:
  `src/Sections/Humans.Gdpr/Controllers/UserController.cs:27`.
- **Every declared category is either erased in full or states a lawful basis**:
  `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:80`, over instances built
  uninitialised at `tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs:32` — so
  a declaration may not touch instance state, the database or the clock.

## 5. Seams

- **Erasure loops the chain outside.** `EraseForUserAsync` takes one id and Users loops the
  merge chain (`AccountDeletionService`). peterdrier/Humans#1704, which would have resolved
  chains inside Users, is closed.
- **The coverage gate is vacuous for a section that never implements the contract at all.**
  The coverage tests enumerate implementers by reflection; the only guardrail is prose in
  `docs/architecture/design-rules.md`. The ledger row that tracked it was closed in the
  Peter-led ledger review, so it is untracked by decision.

## 6. Deliberately not done

- **No caching decorator.** An export is a one-off download of live personal data.
- **No repository, `DbContext` or owned table.** The moment the orchestrator reads a
  table it is duplicating a contributor.
- **No admin route that exports someone else's data.**
- **Gdpr does not register the contributor forwarding factories.** Each belongs beside the
  service that owns it.
- **The `/Consent/MyData` and `/Guest` pages are not here.** They are Consent's and
  Onboarding's; they link to this section's actions.
- **The deletion lifecycle is not here.** Grace period, ticket hold, audit entry and
  confirmation email belong to `AccountDeletionService` under Users.
- **The export does not refuse an undeclared portion.** Dropping it would hand the person
  an incomplete copy; the log line surfaces the erasure gap.
- **The fan-out is not parallel.** Safe, but buys nothing at this scale and muddies failure
  attribution and log order.

## Load-bearing weirdness

- **One contract carries both halves.** `IUserDataContributor` declares the export portion,
  the erasure and the retention declaration together, so a section cannot export a
  category without accounting for its deletion in the same interface.
- **Two calls per contributor on export.** `ContributeForIdentityChainAsync` exists for a
  contributor barred from Users (Email, a crosscut) that cannot resolve the merge chain
  itself; its default returns nothing.
- **Almost all of this section's reforge surface score is the fan-out seam** — the
  `AddScoped<IUserDataContributor>` registrations each data-owning section makes. The
  architecture working as designed.
- **`UserController` is named for the routes it took over, not for Users.** It is the
  membership filter's redirect target by controller name, so a rename moves
  `MembershipRequiredFilter`'s exemption list with it.
- **`MailerLiteSubscriber` is an erasure-only portion name.** MailerLite owns no
  user-scoped table, so it never exports the key.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-27 | Prose across the section describes a layout two moves stale — a deleted project, a deleted `DbContext`, a controller the section has | peterdrier/Humans#1540 |
| 2 | 2026-09-17 | The section's own rationale for its shape was false, and the target shape had not caught up with erasure moving in | peterdrier/Humans#1723 |
| 3 | 2026-09-23 | The envelope learned whose account it is and the prose did not; a "logged" check was documented as "enforced" | peterdrier/Humans#1811 |
| 4 | 2026-10-23 | The section took over the member's subject-rights pages and its docs still said it renders nothing | peterdrier/Humans#2148 |
