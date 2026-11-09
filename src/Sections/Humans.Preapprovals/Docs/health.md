# Preapprovals — Target Shape

## 1. What the section does

Finance sets aside a pot of money for an event (travel help, creativity grants) and names the
people who hand it out. Those managers promise individual members a sum up front, either directly
or by approving a member's request, and can later lower, raise or withdraw the promise. A member
sees what they have been promised and until when, and spends it by filing an expense report;
Expenses holds the promise while the report is in flight and uses it up when the report is
approved. Nobody can promise more than the pot holds. Two reminders go out before the filing date;
nothing else happens on its own, so a promise nobody used stays on the books as unused.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "What have I been promised, and what can I still ask for?" | a member | `GET /Preapprovals`, `GET /Preapprovals/{programId}` |
| "I'd like money from this pot" (and "let me change my request") | a member | `POST /Preapprovals/{programId}/Apply` |
| "Which pots exist, how full are they, who is waiting?" | manager, Board, finance | `GET /Preapprovals/Admin`, `GET /Preapprovals/Admin/{programId}` |
| "Promise / decline / withdraw / resize this person's money" | a program's manager, finance | the `Issue`, `IssueMany`, `Approve`, `Decline`, `Revoke`, `Amount` POSTs under `/Preapprovals/Admin/{programId}` |
| "Set up or change a pot and its managers" | finance | `New` and `{programId}/Edit` under `/Preapprovals/Admin` |
| "Which promises can this report spend, and hold / release / use one up" | Expenses | `IPreapprovalService`, `IPreapprovalWrite` |
| "Remind people before their filing date" | the nightly job | `PreapprovalRemindersJob` |
| "What do you hold about me; forget me; this account is now that one" | Gdpr, Users | `IUserDataContributor`, `IUserMerge` on `PreapprovalService` |

## 3. Structure

One service class split by lane (reads and the Expenses transitions, programs, grants, GDPR) over
one repository and one DbContext with four tables. Delivery of notices and emails lives in one
internal helper shared by the service and the reminder pass; the reminder pass is its own small
class so the job does not resolve the whole service. Two controllers: a localized member side and an
English-only manager side under the section's own URL space. A policy handler gates the two
controllers; a resource handler gates each program. The public face is the two interfaces
Expenses calls plus the four records they carry; everything else is internal.

That is what is built. One rule still sits in a controller: that a grant action names a grant of
the route's program (`PREAPPROVALS-2`).

## 4. Invariants

- The ceiling holds: the open, claimed and consumed amounts of a program never sum past its
  ceiling, checked before every write that adds money (`PreapprovalService.Grants.cs:301`) and
  before a ceiling edit (`PreapprovalService.Programs.cs:73`).
- Status moves only along Applied → Open or Declined; Open → Claimed or Revoked; Claimed → Open,
  Consumed or Revoked. Each method checks the state it starts from
  (`PreapprovalService.Grants.cs:160`, `PreapprovalService.Grants.cs:193`,
  `PreapprovalService.Grants.cs:220`, `PreapprovalService.cs:94`, `PreapprovalService.cs:171`).
- Money is committed only on an open program before its deadline
  (`PreapprovalService.Grants.cs:277`); a member applies only while the program is open, accepting
  and before the deadline (`Dtos.cs:42`).
- A fixed-per-person program refuses an explicit amount on issue and approve
  (`PreapprovalService.Grants.cs:286`) and ignores one on apply (`PreapprovalService.Grants.cs:111`).
- A grant's kind cannot change once the program has a grant (`PreapprovalService.Programs.cs:166`).
- One pending application per person per program, across merged ids: the second submit edits the
  first under a process-wide lock (`PreapprovalService.Grants.cs:95`,
  `PreapprovalService.Grants.cs:124`).
- A manager of one program never acts on another's grants: a grant route whose grant is not in the
  route's program is 404 (`PreapprovalsAdminController.cs:175`).
- Only Admin and FinanceAdmin create or edit a program (`ProgramOperationRequirement.cs:37`); Board
  sees every program and changes nothing (`ProgramOperationRequirement.cs:33`).
- The reminder pass writes only its sent-markers and never a grant
  (`PreapprovalDeadlineReminders.cs:79`).
- Erasure deletes pending applications and manager rows and keeps every decided grant
  (`PreapprovalRepository.cs:170`).

## 5. Seams

- **Program list for a member who manages nothing and holds nothing.** The member page has no nav
  entry by design; people arrive by link.

## 6. Deliberately not done

- **No caching decorator.** A few programs a year; every read is one query.
- **No timer on the deadline.** A passed date is read, never acted on.
- **No Contracts leaf.** Nothing Preapprovals references reaches Expenses, so no cycle asks for one.
- **No localization on `/Preapprovals/Admin`.** Operator pages are exempt.

## Load-bearing weirdness

- **Expenses' transitions take an `afterCommit` callback.** They run inside Expenses'
  `TransactionScope`; audit and notices must wait for that commit, so the caller collects and runs
  them.
- **`ProgramsWithChildren` uses `AsNoTrackingWithIdentityResolution`.** The grant → program back
  references make the graph cyclic, which plain no-tracking queries refuse.
- **The repository saves grants through `Scalar`.** A detached program graph must never ride along
  on a grant save.
- **Completion effects ignore browser cancellation.** Once a grant commits, its audit and notices
  run with `CancellationToken.None` so a closed tab cannot drop them.
- **The program form drops a per-person amount for a per-grant kind before it posts.** The field is
  only hidden when the kind changes, so it keeps its value; `ProgramFormViewModel.ToSave` clears
  it rather than letting the service refuse a value the admin can no longer see.
- **Madrid dates.** "Today" is the Europe/Madrid date, the zone the association books in.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-11-09 | First doctoring: the amount-changed email preview shows the old amount; admin visibility asks the program handler; the invariant doc states rules, not review rounds | pending |
