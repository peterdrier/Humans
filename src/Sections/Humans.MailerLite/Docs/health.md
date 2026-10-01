# MailerLite — Health

Derived fresh each `/section-doctor` run, before any scan. The run file for each date holds the
findings; this file holds the target shape and the run history.

## 1. What the section does

Keeps the collective's MailerLite account and Humans in agreement about who may be emailed
marketing, and which mailing lists each person belongs on.

Two directions, both admin-driven:

- **Inbound.** People who signed up on the public website land in MailerLite's `Website` list.
  An admin previews what pulling that list into Humans would do — create a person, attach an
  address to someone who already exists, flip a marketing preference, or leave it alone — and
  then commits it. The preview also finds people whose marketing opt-in was set by a bad
  earlier import and offers to take it back to "never said".
- **Outbound.** Humans decides who belongs on each of a fixed set of lists ("has a shift",
  "holds a ticket", "opted in to marketing", …) and pushes that membership into the matching
  MailerLite list, adding and removing people so the list matches. An admin can push one list,
  push all of them, or open a per-list screen that shows who would be added and removed before
  pushing. An optional schedule can push all of them unattended.

When a person is erased under GDPR, their MailerLite subscriber goes with them.

## 2. The shapes

| # | Question the section answers | Surface today |
|---|---|---|
| 1 | What does the remote account look like right now? | dashboard (`GET /MailerLite/Admin`), `Refresh` (POST) |
| 2 | What would pulling the website list into Humans do? | `Import` (GET) |
| 3 | Do it. | `Import/Commit` (POST) |
| 4 | Who belongs on list X, who is on it, what changes? | `Audiences/{key}/Debug` (GET) |
| 5 | Push list X / push them all. | `Audiences/{key}/Sync` (POST), `SyncAll` (POST), the opt-in Hangfire job |
| 6 | Forget this person at the processor. | `MailerLiteGdprContributor.EraseForUserAsync` |

Shapes 1–5 are one admin screen apiece or a button on one; shape 6 is a fan-out target with
no UI. Nothing outside the section asks MailerLite anything.

## 3. Structure

The layout those shapes imply:

- **No cross-section surface.** The section is a leaf: it consumes Users, Tickets, Shifts,
  AuditLog and the GDPR fan-out, and nothing calls into it. Its only public types are the ones
  the host discovers by convention (the `Section` entry point and its siblings).
- **One port to the remote** — `IMailerLiteService` / `MailerLiteClient`: paged reads held in a
  Singleton snapshot, and exactly the writes shapes 5 and 6 need. Every list write is refused
  unless the list is one of ours.
- **One inbound orchestrator** — plan then apply, stateless between them, re-pulling the remote
  at apply time so a stale preview cannot be committed blind.
- **One outbound orchestrator** — compute a list, diff it against the remote list, apply,
  record. Dashboard stats are the same compute without the apply; the scheduled job is a shim
  that calls it.
- **One list definition per list**, each answering "which user-ids" and nothing else, over
  cross-section read interfaces. What they share — the marketing opt-out exclusion, the
  ticket-holder set, the shift-signup walk — sits once, above them.
- **One suppressed-status rule** (`MailerLiteSubscriber.IsSuppressed`), read by the sync, the
  stats and the debug preview.
- **One table**, `mailerlite_sync_states`: the current state of each list's last push plus the
  import's, behind the section's repository.
- **Views** are operator English with no resource set. A view takes the service's own record
  when that record already has the shape it renders; a view model exists only where the page
  needs a shape no record has.

## 4. Invariants

The behavioural invariants live in [`MailerLite.md`](MailerLite.md); this list names where the
ones the target leans on are enforced.

- Every write that names a list is refused unless the list's name starts `"Humans - "` —
  `MailerLiteClient.cs:69` (create) and `MailerLiteClient.cs:172` (assign, unassign, bulk).
  The sync refuses a mis-prefixed audience before any write, because its per-write catches
  would otherwise turn the client's refusal into an error count —
  `MailerLiteAudienceSyncService.cs:74`.
- Every list drops people who explicitly said no to marketing —
  `MailerLiteAudienceBase.cs:28`.
- A subscriber MailerLite reports as unsubscribed, bounced or spam-flagged is never added to a
  list — `MailerLiteAudienceSyncService.cs:113`, reading `MailerLiteSubscriber.cs:29`.
- The import reads only the `Website` list and refuses to run without it —
  `MailerLiteImportService.cs:342`.
- A marketing reset re-checks the person at apply time, so a preference changed after the
  preview is not clobbered — `MailerLiteImportService.cs:379`.
- A push or import an admin started finishes even if the admin leaves the page —
  `MailerLiteAdminController.cs:167`, `:194`, `:268`.
- Every admin route is admin-only — `MailerLiteAdminController.cs:14`.
- Erasure deletes the subscriber under every verified address and the primary —
  `MailerLiteGdprContributor.cs:71`.

## 5. Seams

- **`DriftReport.HumansOptedInMlAbsent`** — the second half of the drift report, specified in the
  dashboard's markup and never computed (left for Peter on the 2026-08-25 run).
- **Idempotent `BulkImportSubscribersToGroupAsync` counts** — the client reports every successful
  per-email upsert as `Created`, so a re-push of an unchanged list reports non-zero creations.
  `Updated` and `Duplicates` are wired to zero.

Reserve the places; don't build them.

## 6. Deliberately not done

- **No caching decorator.** The client is a Singleton holding its own snapshot; a decorator
  would be caching the same remote twice.
- **No resource set.** Admin-only operator English
  (`memory/code/localization-admin-exempt.md`).
- **No unique index on `mailerlite_sync_states.Key`.** A striped app-level lock covers it at one
  server, and the read path takes the newest row rather than 500ing on a duplicate.
- **No history rows.** The sync-state table is current state; the audit log is the history.
- **No webhook / incremental import.** Plan-and-apply over a full pull is cheap for a small user
  base.

## Load-bearing weirdness

- **The debug screen resolves notification-target emails from cached `UserInfo` rather than
  calling `IUserEmailService`.** A deliberate duplication of that service's rule, pinned by
  `MailerLiteAudienceDebugSnapshotBuilderTests.Build_NoDbQueries_OnlyCachedUserInfoAndMlReads`
  — the screen must render without DB queries. Keep the two in step by hand.
- **`BadImportCutoff` is a hardcoded instant.** One-time GDPR remediation for a specific bad
  import, not a policy knob.
- **The apply paths deliberately ignore the request cancellation token** — an admin closing the
  tab must not leave a list half-pushed (nobodies-collective/Humans#950).
- **Assign/unassign do not invalidate the client's subscriber snapshot.** The sync holds its own
  snapshot for the whole run and per-write invalidation would burn the rate limit.
- **The `Website` list is read by name and never written to.** It is a source; the `"Humans - "`
  write guard exists so nothing can start writing to it.
- **The dashboard builds a full import plan on every load** to compute the drift row. Expensive
  for a dashboard, cheap at this scale, and the only source of that number.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-25 | Debug picker always showed the last list; Apply confirmed twice; dead client surface deleted; shared audience rules defined once | peterdrier/Humans#1513 |
| section-doctor | 2026-10-01 | The section has no cross-section surface; admin views render the service records; docs and comments say what the code does | pending |
