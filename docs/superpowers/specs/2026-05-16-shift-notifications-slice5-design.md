# Shift Notifications & Coordinator Digest — Design (Slice 5)

Spec-completion for [nobodies-collective#162](https://github.com/nobodies-collective/Humans/issues/162) — currently labelled `blocked:spec-incomplete`. This doc fills the gaps that earned that label and pivots the original coordinator path from per-signup email to a weekly conditional digest.

## Problem

Shift management slices 1–3 are complete (#135). Slice 5 was scoped in `docs/specs/2026-03-16-shift-management-design.md` as seven bullet points — not enough to implement. Specifically the original spec did not answer:

1. Email-template content for each trigger (subject + body skeleton)
2. Trigger fire semantics (immediate vs batched)
3. Coordinator recipient resolution (which coordinators, what if zero)
4. "Schedule change" scope (which mutations count)
5. `DutySignupId` FK behavior (cascade vs set-null)
6. Opt-out UX (where + which triggers respect it)
7. Cron cadence (reminder, GC)
8. Reminder edge cases (late signups)
9. Voluntell email vs confirmed email (same template?)
10. Re-fire-after-bail-and-re-confirm dedup behavior
11. **NEW:** How to handle coordinators who miss the initial notification

This spec answers all 11.

## Goal

Volunteers receive timely, immediate emails about their own signups (confirmed, refused, bailed, cancelled, reminder, voluntell, schedule change). Coordinators receive a low-noise weekly digest of pending items awaiting their review, only when there's actually something to do and they haven't already been engaging with the queue.

No coordinator gets a daily email about new signups. The dashboard tile and in-app notification meter remain the primary pull surfaces; the digest is the backstop for items that slip through.

## Non-Goals

- Real-time push notifications (web push, SMS) — out of scope.
- Discord / Slack integration — separate issue (#82).
- Volunteer-side digest of "your upcoming shifts" — covered by per-shift reminder; no roll-up planned.
- Coordinator coverage-gap alerts via email — the existing in-app `ShiftCoverageGap` actionable notification stays as the surface; no email path added.
- Auto-approve / auto-refuse after deadline — too aggressive; out of scope.

## Privilege Grant

Author of #162 is **peterdrier** ✓. No additional grant needed per `memory/process/issue-fetch-protocol`.

## Definitions

| Term | Meaning |
|---|---|
| **Coordinator** | A user with a coordinator role assignment on a team (department or sub-team). Resolved via `IRoleAssignmentService.GetUserCoordinatedTeamIdsAsync(userId)`. |
| **Pending item** | A `ShiftSignup.Status = Pending` OR a `TeamJoinRequest.Status = Pending` on a team the user coordinates. |
| **Engaged coordinator** | A coordinator who has changed the status (approved or refused) of at least one pending item in the last 7 days. Detection query: `shift_signups.UpdatedAt + ReviewedByUserId` (same for `team_join_requests`). |
| **Stale item** | A pending item whose `CreatedAt` is ≥ 7 days ago. |
| **DutySignupId** | New FK on `EmailOutboxMessage` pointing to `ShiftSignup.Id`. Enables `(DutySignupId, TemplateName)` dedup per-signup. |
| **Schedule change** | Any mutation to `Shift.StartTime`, `Shift.DayOffset`, `Shift.Duration`, or `Shift.IsAllDay`. **Not**: `Description`, `MaxVolunteers`, `MinVolunteers`, `AdminOnly`. |

## Triggers — overview

Eight triggers in total: seven immediate volunteer-facing + one weekly conditional coordinator-facing digest. Replaces the original #162 trigger #5 (per-signup coordinator email) with the digest.

| # | Trigger | Recipient | Fire timing | Template name |
|---|---|---|---|---|
| 1 | Signup confirmed | Volunteer | Immediate on `Status → Confirmed` | `SignupConfirmed` |
| 2 | Signup refused | Volunteer | Immediate on `Status → Refused` | `SignupRefused` |
| 3 | Signup bailed | Volunteer | Immediate on volunteer's own bail (`Status → Bailed`) | `SignupBailed` |
| 4 | Shift cancelled | All volunteers with non-Cancelled signups | Immediate on shift / rota delete | `ShiftCancelled` |
| 5 | Shift reminder | Confirmed volunteers | Hangfire job, configurable lead time | `ShiftReminder` |
| 6 | Voluntell | Volunteer (target) | Immediate on insert with `Enrolled = true` | `Voluntell` |
| 7 | Schedule change | Confirmed / pending volunteers | Immediate on `Shift` mutation of any field in the "schedule change" set | `ScheduleChange` |
| 8 | Coordinator pending digest | Each coordinator | Weekly conditional (see §Coordinator Pending Digest) | `CoordinatorPendingDigest` |

## Volunteer-facing emails (immediate)

All seven trigger via the existing `IEmailOutboxService` synchronous enqueue path inside the service method that performs the state transition. None of them go through Hangfire (except #5, see below).

### Dedup per signup

`EmailOutboxMessage` gains a nullable `DutySignupId` FK to `ShiftSignup.Id` (FK behavior: `ON DELETE SET NULL` — keep the audit trail when the signup is later deleted). Composite unique index on `(DutySignupId, TemplateName)` where both are non-null, applied to the immediate-fire triggers (#1, #2, #3, #6).

For trigger #4 (Shift cancelled): one row per affected signup; same `(DutySignupId, TemplateName)` dedup naturally prevents double-fire if the shift is "cancelled" twice.

For trigger #5 (Shift reminder): same `(DutySignupId, TemplateName)` dedup — once a reminder fires for a signup, no second reminder regardless of mutations.

For trigger #7 (Schedule change): dedup is **not** permanent — must fire on each real change. Composite key extended to `(DutySignupId, TemplateName, ShiftLastModifiedAt)` where the last column is captured at enqueue time, allowing re-fire when the shift mutates again. Debounce: if the most recent enqueue for this signup was less than 5 minutes ago, skip (handles rapid-sequence edits).

### Re-fire after bail-and-re-signup

Out-of-the-box correct. A bailed signup is a discrete row that stays in the table with `Status = Bailed`. If the volunteer signs up again, a **new** `ShiftSignup` row is created with a fresh `Id`, so the dedup key is fresh and the new signup fires its own confirmation email. No special-case logic needed.

### Opt-out

`VolunteerEventProfile.SuppressScheduleChangeEmails` already exists in the schema and is honoured only by trigger #7. Triggers #1, #2, #3, #4, #5, #6 are operational and not opt-out-able (you can't sign up for a shift and decline to be told when it's cancelled).

UX surface for the opt-out toggle: existing volunteer profile edit page. New row in the "Communication preferences" section labelled `Suppress schedule change emails` with a short hint.

Per-email footer unsubscribe link with a tokenised one-click URL is **deferred** to a future iteration — adding it requires a new token-issuance path; the in-page profile toggle is sufficient for v1.

## Coordinator Pending Digest (weekly conditional)

A single weekly email, conditional on the state of the recipient's pending-item queue.

### Fire algorithm

Pseudocode for the worker run inside the `CoordinatorPendingDigestJob`:

```text
For each user U who holds at least one Coordinator role:
    tz ← U.ProfileTimeZone ?? EventSettings.TimeZoneId ?? "UTC"
    localNow ← now() in tz
    if (localNow.DayOfWeek, localNow.Hour) ≠ (Tuesday, 17): continue
    if alreadyFiredForUserThisIsoWeek(U): continue

    pendingShiftSignups ← IShiftManagementService.GetPendingForCoordinator(U.Id)
    pendingJoinRequests ← ITeamService.GetPendingJoinRequestsForCoordinator(U.Id)
    items ← pendingShiftSignups ∪ pendingJoinRequests

    if items.Count == 0: continue                              // (a) nothing to do

    stalest ← min(item.CreatedAt for item in items)
    hasStale ← (now - stalest) ≥ 7 days
    isEngaged ← coordinatorResolvedAnyItemInLast(7 days, U.Id)

    if (¬hasStale) ∧ isEngaged: continue                       // (b) engagement skip

    enqueueEmail(U, CoordinatorPendingDigest, items)
    markFiredForIsoWeek(U)
```

### Why Tuesday 17:00

- Monday-morning digests pile up with everyone else's "weekly status email" Monday traffic — easy to swipe-archive.
- Tuesday late-afternoon: coordinator has three weekday afternoons remaining to act; volunteers expecting confirmations get them by Thursday at the latest.
- Local TZ resolution: per coordinator, falls back to event TZ if no profile TZ set. Hangfire job runs hourly at :00 and only fires for coordinators whose local TZ just transitioned into the Tuesday 17:00 window in the past hour.

### Engagement detection

A coordinator is "engaged" in the last 7 days if there exists at least one row in `shift_signups` OR `team_join_requests` such that:

```
ReviewedByUserId = U.Id AND UpdatedAt ≥ now - 7 days AND Status ∈ {Confirmed, Refused}
```

The query is two indexed lookups; cost is bounded by the number of items reviewed per week per coordinator. Index suggestion (review at implementation): `(ReviewedByUserId, UpdatedAt) WHERE Status ∈ ('Confirmed', 'Refused')` on each table.

### Items in the digest

Two sections in the body:

1. **Shift signups awaiting approval** — rows: shift name (linked to approval page), volunteer name, date, time, days-pending. Sorted oldest-first.
2. **Team join requests** — rows: human name (linked to join-request page), team name, days-pending. Sorted oldest-first.

If only one section is non-empty, the empty section is omitted (no "0 team join requests" line).

### Dedup

Digest dedup key: `(CoordinatorUserId, TemplateName="CoordinatorPendingDigest", IsoWeekYear)`. One digest per coordinator per ISO calendar week. Prevents double-fire if the job is restarted within the same week.

### Cross-section dependency

Adds two new method declarations:

- `IShiftManagementService.GetPendingShiftSignupsForCoordinatorAsync(Guid userId, CancellationToken ct)` returns a flat list `(ShiftSignupId, RotaId, TeamId, ShiftStart, VolunteerUserId, CreatedAt)`. Touches surface budget (+1).
- `ITeamService.GetPendingJoinRequestsForCoordinatorAsync(Guid userId, CancellationToken ct)` returns a flat list `(JoinRequestId, TeamId, UserId, CreatedAt)`. Touches surface budget (+1).

Both services are called from the digest job's worker, not from each other — no cross-section service-to-service call.

## Data Model

Single migration: add `DutySignupId UUID NULL` column + FK + composite index to `email_outbox_messages`.

```sql
ALTER TABLE email_outbox_messages
  ADD COLUMN "DutySignupId" UUID NULL;

ALTER TABLE email_outbox_messages
  ADD CONSTRAINT "FK_email_outbox_messages_shift_signups_DutySignupId"
    FOREIGN KEY ("DutySignupId") REFERENCES shift_signups ("Id")
    ON DELETE SET NULL;

CREATE UNIQUE INDEX "IX_email_outbox_messages_DutySignupId_TemplateName"
  ON email_outbox_messages ("DutySignupId", "TemplateName")
  WHERE "DutySignupId" IS NOT NULL
    AND "TemplateName" <> 'ScheduleChange';

-- ScheduleChange uses non-unique index (re-fire allowed)
CREATE INDEX "IX_email_outbox_messages_ScheduleChange_signup"
  ON email_outbox_messages ("DutySignupId", "TemplateName")
  WHERE "TemplateName" = 'ScheduleChange';
```

No other schema changes. `VolunteerEventProfile.SuppressScheduleChangeEmails` already exists. ISO-week dedup for the digest can use an in-memory flag per worker invocation (lookback to `email_outbox_messages` rows for the current ISO week).

## Jobs

| Job | Cadence | Purpose |
|---|---|---|
| `ShiftReminderJob` | Every 15 min | Enqueue `ShiftReminder` for confirmed signups whose shift starts inside the `(now + lead - 15min, now + lead]` window |
| `CoordinatorPendingDigestJob` | Hourly at :00 | Per-coordinator local-TZ Tuesday 17:00 check + conditional enqueue |
| `SignupGCJob` | Daily at 03:00 UTC | Set `Status = Cancelled` on signups whose `Shift` was hard-deleted or whose `Rota.IsVisibleToVolunteers = false` |

All three register at `Program.cs` via the existing Hangfire `IRecurringJobManager` extension. No new infra.

### Reminder edge case

If a confirmed signup is created **after** the reminder window has already opened for its shift, fire the reminder immediately on signup confirmation (synchronous enqueue path, not the cron). The `(DutySignupId, TemplateName="ShiftReminder")` dedup prevents the cron from double-firing the same signup later.

## Localization

New resx keys, six locales each (en, es, ca, it, fr, de):

| Key | Use |
|---|---|
| `Email_SignupConfirmed_Subject` / `_Body` | Trigger #1 |
| `Email_SignupRefused_Subject` / `_Body` | Trigger #2 |
| `Email_SignupBailed_Subject` / `_Body` | Trigger #3 |
| `Email_ShiftCancelled_Subject` / `_Body` | Trigger #4 |
| `Email_ShiftReminder_Subject` / `_Body` | Trigger #5 |
| `Email_Voluntell_Subject` / `_Body` | Trigger #6 |
| `Email_ScheduleChange_Subject` / `_Body` | Trigger #7 |
| `Email_CoordinatorDigest_Subject` / `_Body_Header` / `_Section_Signups` / `_Section_JoinRequests` / `_Body_Footer` | Trigger #8 (multiple slots for assembly) |
| `Profile_SuppressScheduleChangeEmails_Label` / `_Help` | Opt-out toggle |

Total: ~17 new keys × 6 locales = ~102 string additions. Translations to be authored at implementation time (English drafted in this spec doc; other locales follow project translator convention).

## Testing

### Unit (service layer)

- Each immediate trigger fires exactly once per state transition for the correct recipient (7 tests).
- Dedup prevents double-fire on re-running the same state transition (7 tests).
- `ScheduleChange` re-fires on each real change but debounces within 5 minutes (3 tests: real-change-fires, no-change-skips, debounce-window).
- Opt-out (`SuppressScheduleChangeEmails`) is honoured only by trigger #7 (1 test).
- Voluntell template is distinct from SignupConfirmed (1 test asserting template name).

### Unit (digest job)

- Fires only on Tuesday 17:00 local TZ.
- Skips if zero pending items.
- Skips if all items are <7 days old AND coordinator engaged in last 7 days (Reading B).
- Fires if at least one item is ≥7 days old (regardless of engagement).
- Fires if coordinator has not engaged in 7 days (regardless of staleness).
- Dedups per coordinator per ISO week.
- Local TZ resolves correctly through `ProfileTimeZone` fallback chain.

### Integration

- End-to-end: signup → confirmation → email outbox row appears with correct template + dedup key.
- End-to-end: pending signup → wait 7 days → digest job runs → email outbox row appears for the coordinator.
- Migration applies cleanly forward + back; existing email rows preserved with `DutySignupId = NULL`.

## Open Items

| Item | Plan |
|---|---|
| Per-email footer unsubscribe token | Deferred. v1 uses the in-page profile toggle. |
| Discord / Slack channel push | Deferred — separate issue #82. |
| Volunteer-side digest of upcoming shifts | Not planned — per-shift reminder is sufficient. |
| Email template HTML styling | Deferred to implementation. Use existing project email layout. |
| Reminder lead-time bounds | Inherits `EventSettings.ReminderLeadTimeHours` field (already exists). No new bounds added. |
| Translation authoring | English subjects + body skeletons in this doc; full 6-locale translations at implementation time. |

## Implementation slicing suggestion

If #162 is too large for one PR, this spec splits naturally into three:

1. **Schema + volunteer immediate triggers** — `DutySignupId` FK, triggers #1–4 + #6 (the "happy path" of signup state transitions). Independent of cron infra.
2. **Reminder cron + opt-out + schedule-change trigger** — adds `ShiftReminderJob`, trigger #7, opt-out toggle UI. Independent of digest.
3. **Coordinator digest** — adds `CoordinatorPendingDigestJob`, the two cross-section methods on `IShiftManagementService` and `ITeamService`, the digest template. Depends on slice 1 (needs `DutySignupId` infra to be in place for the foreign-key style dedup, though the digest itself uses ISO-week dedup).

Each slice is shippable on its own and reviewable in <500 LoC.
