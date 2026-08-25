# MailerLite — Health

Derived fresh each `/section-doctor` run, before any scan. The run file for each date holds the
findings; this file holds the target shape and the score history.

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
  MailerLite group, adding and removing people so the group matches. An admin can push one list,
  push all of them, or open a per-list screen that shows exactly who would be added and removed
  before pushing.

Nobody who has said "no marketing" is ever put on a list, and anyone MailerLite reports as
unsubscribed, bounced or spam-flagged is left alone. When a person is erased under GDPR their
MailerLite subscriber goes with them.

## 2. The shapes

| # | Question the section answers | Surface today |
|---|---|---|
| 1 | What does the remote account look like right now? | dashboard (`GET /MailerLite/Admin`), `Refresh` (POST) |
| 2 | What would pulling the website list into Humans do? | `Import` (GET) |
| 3 | Do it. | `Import/Commit` (POST) |
| 4 | Who belongs on list X, who is on it, what changes? | `Audiences/{key}/Debug` (GET) |
| 5 | Push list X / push them all. | `Audiences/{key}/Sync` (POST), `SyncAll` (POST), the Hangfire job |
| 6 | Forget this person at the processor. | `MailerLiteGdprContributor.EraseForUserAsync` |

Shapes 1–5 are one admin screen apiece or a button on one; shape 6 is a fan-out target with
no UI.

## 3. Structure

The layout those shapes imply:

- **One port to the remote** — `IMailerLiteService` / `MailerLiteClient`: paged reads held in a
  Singleton snapshot, and exactly the writes shapes 5 and 6 need. Every write to a group is
  refused unless the group is one of ours.
- **One inbound orchestrator** — plan then apply, stateless between them, re-pulling the remote
  at apply time so a stale preview cannot be committed blind.
- **One outbound orchestrator** — compute a list, diff it against the group, apply, record.
  Stats for the dashboard are the same compute without the apply.
- **One list definition per list**, each answering "which user-ids" and nothing else, over
  cross-section read interfaces. Everything they share — the marketing opt-out exclusion, the
  ticket-holder set, the shift-signup set — belongs in exactly one place above them.
- **One suppressed-status rule**, named once and read by the sync, the stats and the debug
  preview. Every extra copy of it is another chance for the preview to lie about the apply.
- **One table**, `mailerlite_sync_states`: the current state of each list's last push plus the
  import's, behind the section's repository.
- **Views** are operator English with no resource set, over view models the controller shapes.

## 4. Invariants

The behavioural invariants live in [`MailerLite.md`](MailerLite.md) and are not restated here.
The ones this target adds:

- A rule that both the apply path and a preview screen depend on is defined once. A preview
  that computes membership differently from the apply is a preview that lies.
- Every field on a view model is rendered by some view; every method on a service interface has
  a caller. Dead surface here is not free — it is a promise about a remote we do not own.
- The dashboard shows no permanently-blank measurement. A row that can never carry a number is
  worse than no row: it reads as "checked, nothing found".
- A boolean-valued Razor attribute is written `attr="@(cond ? "attr" : null)"`, never
  `attr="@cond"`.

## 5. Seams

- **`DriftReport.HumansOptedInMlAbsent`** — the second half of the drift report, specified in the
  dashboard's markup and never computed. It needs a count of Humans-side marketing opt-ins whose
  address is absent from MailerLite, which today has no read to hang off.
- **Idempotent `BulkImportSubscribersToGroupAsync` counts** — the client reports every successful
  per-email upsert as `Created`, so a re-push of an unchanged list reports non-zero creations.
  `Updated` and `Duplicates` are wired to zero.

Reserve the places; don't build them.

## 6. Deliberately not done

- **No caching decorator.** The client is a Singleton holding its own snapshot; a §15 decorator
  would be caching the same remote twice.
- **No resource set.** Admin-only operator English; `SectionTypesTakeNoStringLocalizer` makes
  adding a `Localizer[…]` call fail the build rather than silently binding to the host's
  `SharedResource`.
- **No unique index on `mailerlite_sync_states.Key`.** A striped app-level lock covers it at one
  server, and the read path tolerates a duplicate rather than 500ing.
- **No history rows.** The sync-state table is current state; the audit log is the history.
- **No webhook / incremental import.** Plan-and-apply over a full pull is what 500 people costs.

## Load-bearing weirdness

- **The debug screen resolves notification-target emails from cached `UserInfo` rather than
  calling `IUserEmailService`.** That is a deliberate duplication of that service's rule, pinned
  by `MailerLiteAudienceDebugSnapshotBuilderTests.Build_NoDbQueries_OnlyCachedUserInfoAndMlReads`
  — the screen must render without DB queries. Collapsing it into the service call would break
  the pin. Not a defect; keep the two in step by hand.
- **`BadImportCutoff` is a hardcoded instant.** One-time GDPR remediation for a specific bad
  import, not a policy knob.
- **The apply path deliberately ignores the request cancellation token** — an admin closing the
  tab must not leave a group half-pushed (nobodies-collective/Humans#950).
- **Assign/unassign do not invalidate the client's subscriber snapshot.** The sync holds its own
  snapshot for the whole run and per-write invalidation would burn the rate limit.
- **The `Website` group is read by name and never written to.** It is a source; the `"Humans - "`
  write guard exists so nothing can start writing to it.

## Score history

| Date | Run | reforge | loc | cogP95 | cogMax |
|---|---|---|---|---|---|
| 2026-08-25 | [2026-08-25-MailerLite](../../../../docs/health/runs/2026-08-25-MailerLite.md) (peterdrier/Humans#1513) | 278 | 2771 | 17 | 38 |
