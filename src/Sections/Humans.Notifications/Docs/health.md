<!-- freshness:triggers
  src/Sections/Humans.Notifications/**
  src/Sections/Humans.Notifications.Contracts/**
  tests/Humans.Notifications.Tests/**
-->

# Notifications — target shape

Derived fresh each section-doctor run, before any scan. History at the bottom.

## 1. What the section does

When something happens elsewhere that a human should know about, the section that saw it
hands Notifications a sentence and a list of people. Notifications keeps that sentence until
the people have seen it and — where it asks for work — until someone has done the work. Each
human sees their own pile on the inbox page and behind the bell at the top of every page, can
clear items off it, and anything that has aged out is thrown away overnight.

Beside the pile it shows a second thing that looks the same but is not stored at all: live
counts of work waiting in queues other sections own ("consent reviews pending"), asked of those
sections on each render and held for at most two minutes.

An agent polling on a human's behalf can read the same unread pile and counts through the
machine API; reading never changes anything.

Two rules give the section its character: seeing is personal but doing is shared, and a human
can mute the chatter but never the work.

## 2. The shapes

| # | Question a caller is asking | Surface answering it |
|---|---|---|
| 1 | "Tell these humans something happened." | `INotificationEmitter.SendAsync` (explicit recipients) · `INotificationService.SendToRoleAsync` (everyone holding a role) |
| 2 | "That condition is fixed — clear the alerts about it." | `INotificationAutoResolve.ResolveBySourceAsync` (one human, one source) · `ResolveBySourceKeyAsync` (every recipient, one source entity) |
| 3 | "What is on my pile?" | `GET /Notifications` · `GET /Notifications/Popup` · the `NotificationBell` chrome component |
| 4 | "I have dealt with this." | `MarkAllRead` · `BulkResolve` · `BulkDismiss` · `ClickThrough` from the pages; `Resolve` · `Dismiss` · `MarkRead` routed but reached by no page |
| 5 | "How much work is waiting for someone like me?" | `NotificationMeterProvider.GetMetersForUserAsync` |
| 6 | "What is unread for this key's owner?" | `INotificationInboxRead.GetUnreadInboxAsync`, served by Backdoor at `/api/backdoor/notifications` |
| 7 | "Throw away what has aged out." | `INotificationRetention.PurgeExpiredAsync`, driven nightly by `CleanupNotificationsJob` |
| 8 | "These humans' badges are stale now." | `INotificationService.InvalidateBadgeCachesForUsers` |
| 9 | "Give me / erase everything you hold about this human." | `IUserDataContributor` on `NotificationInboxService` |
| 10 | "These two accounts are one human — fold them." | `IUserMerge.ReassignAsync` on `NotificationService` |

Shapes 1, 2, 7, 8 and 10 are the cross-section write contract; 6 is the one cross-section read;
3–5 are the section's own pages.

## 3. Structure

- **One contracts leaf** carrying shapes 1, 2, 7 and 8 plus the three enums their signatures
  name, because a dozen sections emit and must not reference the whole section. Shape 6 lives
  in the section's own `Contracts/` folder: its only consumer, Backdoor, already references
  the section project, as it does for every section it serves.
- **One dispatch path.** Build rows, apply the preference filter, persist, evict the badges of
  whoever received something. Explicit recipients get a row each; a role gets one shared row.
- **One inbox service** for shapes 2, 3, 4, 7 and 9: read models, per-row transitions, the
  retention cutoffs, the GDPR contribution, the one cached read (badge counts).
- **One meter provider** for shape 5, reaching every count through the owning section's read
  interface.
- **One thin adapter** for shape 6, composing the inbox's unread tab with the meters.
- **One repository**, the only code touching `notifications` / `notification_recipients`.
- **One controller** that parses, calls and formats.
- **One row shape between service and view**, carrying only what a page renders.

## 4. Invariants

- Only `NotificationRepository` touches the section's tables — `src/Humans.Analyzers/Internal/Rules/TableOwnershipRule.cs` (HUM0025).
- An `Informational` emit skips a recipient whose inbox preference for the source's category is
  off; an `Actionable` emit never does — `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs:57`, `src/Sections/Humans.Notifications/Services/NotificationService.cs:83`.
- An emit whose recipients are all filtered out writes nothing and logs why — `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs:89`, `src/Sections/Humans.Notifications/Services/NotificationService.cs:95`.
- Resolution is stored on the notification, so resolving clears it for every recipient; read
  state is stored on the recipient row — `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:62`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:110`.
- An `Actionable` notification cannot be dismissed, singly or in bulk — `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:83`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:176`.
- Only a recipient can resolve, dismiss, mark read or click through a notification — `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:54`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:80`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:103`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:152`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:205`.
- Every page route acts on the signed-in human's own pile and nobody else's — `src/Sections/Humans.Notifications/Controllers/NotificationsController.cs:21`.
- Every transition made on a human's behalf evicts the badge cache of each user it touched —
  `src/Sections/Humans.Notifications/Services/NotificationInboxService.cs:103`; the nightly purge is the exception (seams).
- The purge deletes resolved rows past 7 days, unresolved informational rows past 30 days and
  unresolved rows of retired sources, and nothing else — `src/Sections/Humans.Notifications/Services/NotificationInboxService.cs:214`.
- Meters are computed on read and never stored — `src/Sections/Humans.Notifications/Services/NotificationMeterProvider.cs:230`.
- The machine read never mutates: it calls only the inbox's read and the meter provider — `src/Sections/Humans.Notifications/Services/NotificationInboxRead.cs:22`.

## 5. Seams

Specified or known, not built; reserved, not ranked:

- **The Notification Board redesign** (`Docs/features/notification-board.md`, approved) replaces
  the stored inbox with an in-memory board of section-published entries and retires the
  `NotificationSource` enum. Until its phase 4 lands, `notification-inbox.md` describes what runs.
- **The purge leaves badges stale** for up to the two-minute TTL: the three delete methods
  report counts, not affected users.
- **Role fan-out takes no `sourceKey`**, so `ResolveBySourceKeyAsync` can never clear it.
- **Dispatch is described as fire-and-forget but is not wrapped**; an emit failure reaches the
  caller.

## 6. Deliberately not done

- **No caching decorator.** Both services cache one thing and evict it in-band on their own
  writes; a decorator would sit between a service and its own invalidation.
- **No real-time push.** A two-minute badge is enough at this scale.
- **No stored meter.** A slow count is the owning section's to fix with a narrow read.
- **No `GroupKey`.** Shared-versus-individual is decided by which dispatch method is called.
- **Digests stay email.** Summaries are not work items.

## Load-bearing weirdness

- **`NotificationEmitter` is its own type.** `NotificationService` injects
  `IRoleAssignmentServiceRead`, and `RoleAssignmentService` emits; it injects the narrow
  `INotificationEmitter`, whose implementation has no edge back, so the graph cannot close.
  `NotificationService.SendAsync` delegates to the emitter to keep one copy of that path.
- **`NotificationRecipient.UserId` is init-only**, so the account-merge fold is
  remove-then-add rather than an update.
- **No FK and no navigation on `ResolvedByUserId` or `NotificationRecipient.UserId`**
  (nobodies-collective/Humans#992, nobodies-collective/Humans#996); names are stitched from
  `IUserServiceRead`.
- **`ApplicationSubmitted` and `ConsentReviewNeeded` are retired sources**: nothing emits them,
  the purge deletes their unresolved rows, and the inbox's approvals filter still names them
  while any remain.
- **The repository is a Singleton over `IDbContextFactory`** so Singleton-lifetime callers can
  inject it.

## History

| Run | Date | Headline | PR |
|-----|------|----------|----|
| 1 | 2026-08-26 | DI-cycle story corrected; doc names brought in line with the code | peterdrier/Humans#1527 |
| 2 | 2026-09-30 | One row shape; inbox reads stop loading co-recipients | peterdrier/Humans#1866 |
