# RIP: In-App Notification Inbox (#244)

Generated: 2026-03-31

## 1. Vision & Purpose

### Elevator Pitch

**For** admins, coordinators, and team leads
**Who** are drowning in email notifications and lack a shared view of "what needs handling"
**The** notification inbox
**Is a** built-in alert and task channel
**That** gives every user a central "what needs my attention" view with shared resolution for group-targeted items
**Unlike** email, which creates N independent copies with no shared state
**Our approach** treats notifications as work items — actionable ones can't be dismissed without doing the thing, group-targeted ones resolve for everyone when any member handles it

### Success Criteria

| Criterion | Measure | Priority |
|-----------|---------|----------|
| Operational alerts don't get lost in email | Coordinators resolve actionable items via inbox, not email | Must-have |
| Group work items have single-owner resolution | When Bob resolves a group notification, others see "Resolved by Bob" | Must-have |
| Email volume decreases for operational noise | Existing email notifications can be migrated to inbox-only | Must-have |
| Platform is extensible | New notification sources can be added with <20 lines of dispatch code | Must-have |
| Users control their channel | Per-category inbox/email preferences via existing CommunicationPreference | Should-have |

### Anti-Goals

- **Not a messaging system.** No replies, threads, or conversation. One-way alerts only.
- **Not real-time push.** No WebSocket, no polling. Badge refreshes on page load. Fine at 500 users.
- **Not migrating all 27 email types at once.** This builds the platform; individual sources migrate incrementally.
- **Not a team activity feed (yet).** Laura's request is deferred — data model supports it, but no feed UI in this scope.
- **Not auto-resolution.** V1 is manual "mark resolved." System-triggered auto-resolution is a future enhancement.

---

## 2. Stakeholders & Context

### Stakeholders

| Who | Interest | Impact |
|-----|----------|--------|
| Peter (Admin) | ~15 emails/day already, will grow with shift notifications | Primary user, builder |
| Coordinators | Need shared "who's handling this" visibility within departments | Primary users |
| Board members | Daily digest emails, tier application voting notifications | Users |
| Laura (UX) | Requested team activity feed (deferred, but data model should support it) | Informed |
| Frank | Confirmed priority at March 27 meeting | Informed |

### Constraints

- **Must land before or alongside #162** (shift notification emails) — otherwise shift notifications make the email problem worse.
- **~500 users, single server** — no distributed coordination, no real-time push complexity.
- **Existing outbox pattern** — email delivery already uses queued outbox with Hangfire processing. Notification dispatch should follow the same pattern.
- **Existing CommunicationPreference system** — preferences must extend it, not duplicate it.
- **Existing NavBadges component** — badge display already works for Review/Voting/Feedback queues.

---

## 3. Scope & Requirements

### Capability Map

| # | Capability | Complexity | Priority |
|---|-----------|------------|----------|
| 1 | Notification + NotificationRecipient data model | M | Must |
| 2 | INotificationService dispatch (materialize recipients, check preferences, optionally queue email) | M | Must |
| 3 | Inbox page (list, filter unread/all, resolve, dismiss) | M | Must |
| 4 | Nav badge (bell icon + three-state badge) + notification popup | M | Must |
| 5 | Group resolution (resolve for all recipients when any member acts) | S | Must |
| 6 | Two notification classes: Informational (dismissable) vs Actionable (requires action) | S | Must |
| 7 | Resolution attribution ("Handled by Bob, 2h ago") | S | Must |
| 8 | 7-day retention after resolution + cleanup job | S | Must |
| 9 | First email migration (AddedToTeam → notification) | S | Must |
| 10 | Extend CommunicationPreference with InboxEnabled | S | Should |
| 11 | Preferences UI for inbox/email per category | M | Should |
| 12 | Bulk actions (select multiple, mark all done) | S | Should |
| 13 | Digest frequency (real-time/daily/weekly) | L | Won't (this iteration) |
| 14 | Team activity feed UI | M | Won't (this iteration) |

### Scope Boundary

**In Scope:**
- Domain entities: `Notification`, `NotificationRecipient`
- Enums: `NotificationSource`, `NotificationClass`, `NotificationPriority`
- `INotificationService` interface + `NotificationService` implementation
- `NotificationController` + inbox views
- Nav badge extension (bell icon + three-state badge + popup dropdown)
- `CleanupNotificationsJob` (7-day retention)
- Extend `CommunicationPreference` with `InboxEnabled` column
- Migration of `SendAddedToTeamAsync` as first notification source
- Feature spec in `docs/features/`

**Out of Scope:**
- Team activity feed UI (future issue, query over same data)
- Digest frequency options
- WebSocket / real-time push
- Auto-resolution (system detects action completed)
- Migration of remaining 26 email types
- Notification templates / rich body formatting

**Boundary Decisions:**
- **AddedToTeam as first migration** — it's simple (individual target, informational), already exists, and demonstrates the pattern. Shift notifications (#162) will be the next, higher-value migration once that issue is built.
- **Polling on page load, not real-time** — badge count refreshes when user navigates. At 500 users with a 2-min cache, this is a non-issue.
- **Manual resolution only** — the UI has "Mark resolved" buttons. No system callback to auto-resolve when the underlying task is complete. Simple and sufficient for V1.

### Key User Stories

**1. Coordinator resolves a group notification**
> A shift coverage gap is detected for the Geeks department. All Geeks coordinators see a notification in their inbox: "Shift coverage gap: Saturday 10:00–14:00." Maria clicks through, finds a replacement, and marks the notification resolved. The other coordinators see: "Handled by Maria, 2h ago."

Acceptance criteria:
- Notification appears for all active coordinators of targeted team
- Badge count increments for each coordinator
- Any coordinator resolving it sets ResolvedAt + ResolvedByUserId on the Notification
- All recipients see the resolved state with attribution
- Resolved notification remains visible for 7 days, then cleaned up

**2. Admin dismisses an informational notification**
> An admin receives an informational notification: "Carlos was added to Logistics team." The admin clicks the dismiss (X) button. The notification is marked resolved for all admins.

Acceptance criteria:
- Informational notifications show a dismiss button
- Dismissing resolves the notification (same as resolving, semantically different)
- User can opt out of informational notification categories via preferences

**3. User cannot dismiss an actionable notification**
> A consent coordinator sees: "Consent review needed for Juan García." There is no dismiss button — only "Review" (action URL) and "Mark resolved." The coordinator must handle it.

Acceptance criteria:
- Actionable notifications show action URL button prominently
- No dismiss/X button on actionable notifications
- Actionable notifications cannot be suppressed via preferences
- Email preference for actionable types IS configurable (inbox-only vs inbox+email)

**4. User checks notification inbox**
> A user clicks the bell icon in the nav bar. A popup dropdown opens showing actionable items under "Needs your attention" and informational items under "Recent." They click "See all notifications" in the footer to open the full inbox page, where they can search, filter by type, and toggle between "Unread" and "All" to see resolved items. Each notification shows title, time ago, type tag, and resolution attribution if resolved.

Acceptance criteria:
- Bell icon in nav bar with three-state badge (no badge / success dot / danger count)
- Clicking bell opens popup dropdown (340px, right-aligned, tip arrow)
- Popup shows actionable then informational sections, newest first
- Popup footer links to full inbox page
- Popup empty state: "You're all caught up"
- Full inbox page with search + filter pills, unread/all toggle
- Resolved notifications show "Handled by [Name], [time ago]"
- Empty state when no notifications (both popup and inbox)

---

## 4. Architecture & Technical Decisions

### ADR-1: Materialized Recipients

- **Context:** When targeting "Coordinators of Geeks," resolve membership at creation or query time?
- **Decision:** Materialize at creation. One `NotificationRecipient` row per user at dispatch time.
- **Rationale:** Captures "who was responsible when the alert fired." Makes inbox queries trivial (`WHERE UserId = @me`). Matches email outbox pattern.
- **Consequences:** Late-added team members don't see older notifications. Correct behavior.

### ADR-2: Resolution on Notification, Not Recipient

- **Context:** Where does resolved state live?
- **Decision:** `ResolvedAt` and `ResolvedByUserId` on the `Notification` entity. A notification is one work item — when resolved, it's resolved for everyone.
- **Rationale:** Simple, matches the mental model. NotificationRecipient is just the junction table (who can see it + personal read state).
- **Consequences:** No per-recipient resolution. If a caller needs individual acknowledgment, they create separate Notification records per user. GroupKey concept eliminated.

### ADR-3: Two Notification Classes

- **Decision:** `NotificationClass` enum: `Informational`, `Actionable`

| Behavior | Informational | Actionable |
|----------|--------------|------------|
| Dismiss without action | Yes | No |
| Suppress via InboxEnabled pref | Yes | No |
| Email preference | Configurable | Configurable |
| Action URL | Optional | Required |

### ADR-4: Extend CommunicationPreference, Don't Duplicate

- **Context:** Need per-user control of inbox notifications and email delivery.
- **Decision:** Add `InboxEnabled` (bool, default true) to existing `CommunicationPreference`. Each `NotificationSource` maps to a `MessageCategory`. Dispatch service checks both `InboxEnabled` and `OptedOut` on the user's preference for that category.
- **Rationale:** One preference system, not two. "Adding a column, not cloning the spreadsheet."
- **Consequences:** Notification preference granularity matches email categories (System, EventOperations, CommunityUpdates, Marketing). Fine for V1. If per-source granularity is needed later, add it then.

### ADR-5: Caller Decides Resolution Scope

- **Context:** Some notifications should resolve for the whole group (shift coverage). Others might need individual handling.
- **Decision:** The dispatch service caller decides. Group-targeted notifications (team/role) create one `Notification` shared by all recipients. Individual-targeted notifications create one `Notification` per user.
- **Rationale:** No GroupKey complexity. The caller knows whether this is "any one of you handle this" (one notification, multiple recipients) or "each of you needs to see this" (N notifications, one recipient each).

### Data Model

```
Notification
├── Id (Guid, PK)
├── Title (string, required, max 200)
├── Body (string, nullable, max 2000)
├── ActionUrl (string, nullable, max 500)
├── ActionLabel (string, nullable, max 50) — button text e.g. "Review →", "Approve →"
├── Priority (enum: Normal, High, Critical — stored as string)
├── Source (NotificationSource enum — stored as string)
├── Class (NotificationClass enum: Informational, Actionable — stored as string)
├── TargetGroupName (string, nullable, max 100) — e.g. "Coordinators" for group-targeted
├── CreatedAt (Instant)
├── ResolvedAt (Instant, nullable)
├── ResolvedByUserId (Guid, nullable, FK → User)
└── Recipients → NotificationRecipient[]

NotificationRecipient
├── NotificationId (Guid, FK) ──┐ composite PK
├── UserId (Guid, FK) ──────────┘
├── ReadAt (Instant, nullable)
└── Index: IX_NotificationRecipient_UserId_Unresolved
    → (UserId) WHERE notification is unresolved (for badge count)

CommunicationPreference (extended)
├── ... existing columns ...
└── InboxEnabled (bool, default true)

NotificationSource enum:
├── TeamMemberAdded       → MessageCategory.EventOperations
├── ShiftCoverageGap      → MessageCategory.EventOperations
├── ShiftSignupChange     → MessageCategory.EventOperations
├── ConsentReviewNeeded   → MessageCategory.System
├── ApplicationSubmitted  → MessageCategory.System
├── SyncError             → MessageCategory.System
├── TermRenewalReminder   → MessageCategory.System
└── (extensible as needed)
```

### Component Flow

```
DISPATCH:
  Service/Job calls INotificationService.SendAsync(...)
    → Creates Notification entity
    → Resolves targets: team members, role holders, or individual users
    → Creates NotificationRecipient per user
    → For each recipient: checks CommunicationPreference
      → InboxEnabled = false AND Class = Informational? Skip recipient.
      → OptedOut = false? Queue email via IEmailService.
    → Invalidates NavBadge cache

INBOX:
  NavBadgesViewComponent (extended with "notifications" queue)
    → Two counts: actionable unread + informational unread
    → Badge logic: actionable > 0 → count (danger); informational > 0 → dot (success); else nothing
    → Cached 2 min (existing pattern)

  NotificationController.GetPopup()
    → Unresolved notifications for current user, actionable first, then informational
    → Returns partial view for popup dropdown (340px, max 5 rows scrollable)

  NotificationController.Index(filter, search, source)
    → Unread: WHERE UserId = @me AND ResolvedAt IS NULL
    → All: WHERE UserId = @me (last 7 days of resolved + all unresolved)
    → Search: WHERE Title/Body ILIKE @search
    → Source filter: WHERE Source IN @sources (maps filter pills to NotificationSource groups)
    → Includes Notification nav props, ResolvedByUser display name

  NotificationController.Resolve(notificationId)
    → Sets ResolvedAt = now, ResolvedByUserId = current user
    → Invalidates NavBadge cache

  NotificationController.MarkRead(notificationId)
    → Sets ReadAt = now on this user's NotificationRecipient row
    → Personal action, doesn't affect others

  NotificationController.MarkAllRead()
    → Sets ReadAt = now on all unread NotificationRecipient rows for current user
    → Invalidates NavBadge cache

  NotificationController.Dismiss(notificationId) — informational only
    → Same DB operation as Resolve
    → Returns 403 if notification is Actionable

CLEANUP:
  CleanupNotificationsJob (daily, Hangfire)
    → DELETE Notification WHERE ResolvedAt < now - 7 days
    → CASCADE deletes NotificationRecipients
```

### Technical Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Race condition: two coordinators resolve simultaneously | Low | Low | Both UPDATE succeeds, last write wins for ResolvedByUserId. Acceptable at scale. |
| NotificationSource enum grows unwieldy | Medium | Low | String-stored, easy to extend. Group by MessageCategory in preferences UI. |
| Badge count query perf | Low | Low | Indexed, cached 2 min, ~500 users. |

---

## 5. Prototyping Strategy

This is a well-understood CRUD feature in an established codebase using familiar patterns (EF Core, Hangfire, Bootstrap). No technical unknowns warrant a dedicated prototype phase. The implementation phases below are structured so that Phase 1 is itself a validation — if the data model or dispatch service feel wrong, we adjust before building UI.

### Assumptions (all high-confidence)

| Assumption | Confidence | Rationale |
|-----------|-----------|-----------|
| Materialized recipients is correct model | Likely | Matches email outbox pattern already in use |
| Page-load badge refresh is sufficient | Proven | NavBadges already works this way for 3 queues |
| InboxEnabled on CommunicationPreference is enough granularity | Likely | Matches existing 4-category model; can refine later |
| Existing Bootstrap UI patterns work for inbox | Proven | Feedback inbox, review queue already use similar list patterns |

### Go/No-Go After Phase 1

- **Green:** Data model feels right after writing the dispatch service, badge query is fast → continue to Phase 2
- **Yellow:** Resolution-on-Notification feels wrong for some use case → consider adding resolution table before building UI
- **Red:** (Can't see a scenario — this is standard CRUD in a known stack)

---

## 6. UI Specification (from designer)

Added 2026-04-01 based on designer review.

### Bell Icon (Nav Bar)

Lives in nav bar right section, same row as language selector and user avatar. 32×32 circle button using existing icon button pattern.

**States:**

| State | Visual |
|-------|--------|
| No notifications | Icon at rest, no badge |
| Informational unread only | Small dot indicator (success semantic colour) — no count |
| Actionable unread | Badge with count (danger semantic colour), capped at "9+" |
| Popup open | Circle background slightly brightened (active state) |

Badge sits top-right of circle, 2px border in nav background colour.

Implementation: extend `NavBadgesViewComponent` with two counts (actionable unread + informational unread). Badge logic: actionable > 0 → count badge (danger); else informational > 0 → dot (success); else → nothing.

### Notification Popup

Triggered by clicking bell. Anchored below bell, right-aligned. Small triangular tip arrow on top edge. Closes on outside click or Esc.

- **Width:** 340px fixed
- **Max height:** Scrollable after 5 rows

**Structure (top to bottom):**

1. **Header bar** — primary brand dark green. Left: bell icon + "Notifications" label + pill "N need action". Right: close ✕ button.
2. **Section: "Needs your attention"** — muted separator. Only if actionable items exist.
3. **Actionable notification rows**
4. **Section: "Recent"** — muted separator. Only if informational items exist.
5. **Informational notification rows**
6. **Footer** — light warm surface. Left: "Mark all as read" text link. Right: "See all notifications" outlined secondary button.

**Empty state:** Centred muted bell icon + "You're all caught up." / "No unread notifications." Footer: only "See all notifications" button.

**Data rules:** Only unresolved notifications. Actionable first, then informational. Newest first within each section.

### Notification Row Component

Shared between popup and full inbox. Full-width, padded, bottom border (card divider). Title in primary body serif, body/metadata in secondary sans-serif.

**Left border accent** — 3px, no border-radius:

| Colour | Meaning |
|--------|---------|
| Danger semantic | Urgent/critical actionable |
| Warning semantic | Standard actionable |
| Success semantic | Normal priority actionable (group-targeted) |
| Default border (muted) | Informational |
| Lightest border | Resolved (inbox only) |

**Row anatomy:**

| Position | Content |
|----------|---------|
| Top line | Title (serif, primary) + timestamp (sans-serif, tertiary), space-between |
| Middle | Body copy (sans-serif, secondary) — optional |
| Bottom left | Type tag + optional group avatar cluster |
| Bottom right | Action button or dismiss control |

**Type tag** — existing small pill/badge:

| Tag | Style | Maps to |
|-----|-------|---------|
| "Urgent" | Danger bg + text | Critical priority |
| "Action" | Warning bg + text | High or Normal priority actionable |
| "Info" | Success bg + text | Informational class |

**Group avatar cluster** — for group-targeted notifications only. Overlapping 14px initials circles (team member avatar pattern), max 3 + "+N" overflow, followed by muted `TargetGroupName` label.

**Variant: Actionable row**
- Background: primary card background
- Right: outlined primary button with `ActionLabel` (e.g. "Find cover →", "Review →"). Falls back to "View →".
- No dismiss in popup — resolution only in full inbox

**Variant: Informational row**
- Background: secondary card background (warmer/dimmer)
- Right: text link (if ActionUrl) + ✕ dismiss button (ghost/icon)
- No action button

**Variant: Resolved row** (full inbox only)
- 60% opacity on entire row
- No buttons
- "Handled by [Name], [time ago]" replaces action area — tertiary text, italic

### Full Inbox Page

Standard page layout. H1: "All notifications".

**Toolbar:** Search input (existing component) + filter pills: "All", "Needs action", "Shifts", "Approvals", "Resolved" (existing toggle pill group). "Shifts"/"Approvals" filter by NotificationSource groups.

**Inbox card:** Same card as "Shift Signups" on home page — dark green header + title + unread count badge. "Unread / All" tab toggle in header right.

**Section grouping:** "Needs attention" → "Informational" → "Resolved" with muted separator labels.

**Mobile (< 576px):** Popup becomes full-width sheet. Filter pills scroll horizontally. Row stacks action below body.

---

## 7. Implementation Plan

### Phase 1: Data Model + Dispatch Service (#292)

**Deliverables:**
- Domain entities: `Notification` (including `ActionLabel`, `TargetGroupName`), `NotificationRecipient`
- Enums: `NotificationSource`, `NotificationClass`, `NotificationPriority`
- EF configuration + migration
- `InboxEnabled` column on `CommunicationPreference` + migration
- `INotificationService` interface (Application layer)
- `NotificationService` implementation (Infrastructure layer)
- `CleanupNotificationsJob` (Hangfire daily)
- Unit tests for dispatch logic

**Exit criteria:** Can call `INotificationService.SendAsync()` from any service/job, notifications are persisted with correct recipients (including ActionLabel and TargetGroupName), email is optionally queued.

### Phase 2: Inbox UI + Nav Badge + Popup (#293)

**Deliverables:**
- `NotificationController` (Index, GetPopup, Resolve, Dismiss, MarkRead, MarkAllRead)
- Three-state bell icon in nav bar (no badge / success dot / danger count)
- Notification popup (340px dropdown from bell, sections, empty state)
- Full inbox page with search + filter pills, dark green card header, section grouping
- Notification row component (shared: border accents, type tags, group avatars, action/informational/resolved variants)
- Resolution attribution display ("Handled by Bob, 2h ago")
- Empty states for both popup and inbox
- Localization strings

**Exit criteria:** Bell shows correct badge state, popup opens/closes correctly, full inbox page works with search/filters, all row variants render correctly.

### Phase 3: First Migration + Preferences (#294)

**Deliverables:**
- Convert `SendAddedToTeamAsync` to also dispatch a notification (informational, individual target)
- Extend communication preferences UI with InboxEnabled toggle per category
- Wire InboxEnabled check into dispatch service

**Exit criteria:** When a user is added to a team, a notification appears in their inbox (renders as informational row variant). Users can toggle inbox on/off per category for informational notifications.

### Phase 4: Polish + Bulk Actions (#295)

**Deliverables:**
- Bulk select + resolve/dismiss in full inbox
- Read-on-click (mark as read when user clicks the action URL)
- Mobile-responsive layout (popup → full-width sheet, stacked rows, horizontal pill scroll)
- Group avatar cluster polish (14px, overlapping, +N overflow)
- Keyboard accessibility (focus trap, arrow nav, Esc close)

**Exit criteria:** Full acceptance criteria from issue #244 met.

### Timeline

| Phase | Est. Duration | Dependencies |
|-------|--------------|--------------|
| Phase 1: Data model + dispatch | 1 PR | None |
| Phase 2: Inbox UI + badge | 1 PR | Phase 1 |
| Phase 3: First migration + prefs | 1 PR | Phase 2 |
| Phase 4: Polish + bulk | 1 PR | Phase 3 |

Each phase is one PR to peterdrier/Humans `main`. Four PRs total.

### Post-Ship: Migration Backlog

Once the platform ships, these existing email types are candidates for notification migration (in suggested order):

| Email | NotificationSource | Class | Target | Notes |
|-------|--------------------|-------|--------|-------|
| SendAddedToTeamAsync | TeamMemberAdded | Informational | Individual | Phase 3 (this RIP) |
| SendBoardDailyDigestAsync | — | — | — | Keep as email; digest format doesn't fit notification model |
| SendAdminDailyDigestAsync | — | — | — | Keep as email; same reason |
| #162 shift notifications | ShiftCoverageGap, ShiftSignupChange | Actionable | Team coordinators (group) | Primary use case from issue. Build on notification system from the start. |
| SendSignupRejectedAsync | — | Informational | Individual | Low volume, could migrate |
| SendTermRenewalReminderAsync | TermRenewalReminder | Actionable | Individual | User must act (renew) |

The daily digests are better as email — they're summaries, not individual work items. Shift notifications (#162) should be built directly on the notification system when that issue is implemented.

---

## Appendix: Key Decisions Log

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | Build platform first, migrate sources incrementally | Avoids big-bang rewrite; each migration is a small PR |
| 2 | Team activity feed deferred (option B) | Design data model to support it, don't build feed UI now |
| 3 | Caller decides resolution scope (option C) | Group target = one notification shared. Individual = N notifications. No GroupKey. |
| 4 | Two notification classes: Informational vs Actionable | Actionable can't be dismissed or suppressed. Informational can. |
| 5 | 7-day retention after resolution | Matches email outbox cleanup pattern. Sanity check, not archival. |
| 6 | Resolution attribution | Group resolution shows "Handled by Bob" to all recipients |
| 7 | Resolution on Notification, not Recipient | One work item, one resolution state. Recipient is just the junction. |
| 8 | Extend CommunicationPreference, not new table | One preference system. Add InboxEnabled column. |
| 9 | Materialized recipients at dispatch time | Captures responsibility at time of alert. Same pattern as email outbox. |
| 10 | Page-load badge refresh, no WebSocket | 500 users, 2-min cache. Real-time push is overengineered. |
| 11 | AddedToTeam as first migration | Simple, already exists, demonstrates the pattern |
| 12 | Daily digests stay as email | Summary format doesn't fit notification model well |
| 13 | Popup between bell and full inbox (designer spec, 2026-04-01) | Click bell → popup dropdown (340px, 5 rows). "See all" → full inbox page. Two-tier access. |
| 14 | Three-state bell badge (designer spec) | No badge / success dot (informational only) / danger count (actionable). Distinguishes urgency at a glance. |
| 15 | ActionLabel + TargetGroupName on Notification (designer spec) | Row component needs per-notification action button text and group display name. Set by caller at dispatch. |
| 16 | Left border accent for priority (designer spec) | 3px border: danger/warning/success/muted/lightest. Replaces earlier "red accent / amber" description. |
| 17 | Type tags on rows (designer spec) | "Urgent" / "Action" / "Info" pill badges map to priority+class. |
