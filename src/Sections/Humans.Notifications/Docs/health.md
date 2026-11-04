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
| 8 | "Which sources exist, and which are retired?" | `INotificationSourceContributor`, collected by `NotificationSourceRegistry` at startup |
| 9 | "Give me / erase everything you hold about this human." | `IUserDataContributor` on `NotificationInboxService` |
| 10 | "These two accounts are one human — fold them." | `IUserMerge.ReassignAsync` on `NotificationService` |

Shapes 1, 2, 7 and 8 are the cross-section write contract; 6 is the one cross-section read;
3–5 are the section's own pages; 9 and 10 are the platform fan-outs every people-holding
section answers.

## 3. Structure

- **One contracts leaf** carrying shapes 1, 2, 6, 7 and 8 plus the enums their signatures
  name, because a dozen sections emit and must not reference the whole section. Backdoor reads
  shape 6 from the same leaf.
- **One dispatch path.** Build rows, apply the preference filter, persist.
  Explicit recipients get a row each; a role gets one shared row.
- **One inbox service** for shapes 2, 3, 4, 7 and 9: read models, per-row transitions, the
  retention cutoffs, the GDPR contribution, live badge counts.
- **One meter provider** for shape 5, reaching every count through the owning section's read
  interface.
- **One thin adapter** for shape 6, composing the inbox's unread tab with the meters.
- **One repository**, the only code touching `notifications` / `notification_recipients`.
- **One controller** that parses, calls and formats.
- **One row shape between service and view**, carrying only what a page renders.
- **One outcome shape for a per-row transition**, from repository to controller, carrying
  success, not-found and forbidden.
- **One registry** of every section's sources, built and checked when the host starts.

## 4. Invariants

- Only `NotificationRepository` touches the section's tables — `src/Humans.Analyzers/Internal/Rules/TableOwnershipRule.cs` (HUM0025).
- An `Informational` emit skips a recipient whose inbox preference for the source's category is
  off; an `Actionable` emit never reads the preference — `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs:67`.
- An emit with no recipients, or whose recipients are all filtered out, writes nothing and logs
  why — `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs:57`, `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs:73`.
- Resolution is stored on the notification, so resolving clears it for every recipient; read
  state is stored on the recipient row — `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:151`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:55`.
- A resolve or dismiss acts only on the class it names, so an `Actionable` notification cannot
  be dismissed, singly or in bulk — `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:32`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:87`.
- Only a recipient can resolve, dismiss, mark read or click through a notification — `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:29`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:48`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:89`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:101`, `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:123`.
- Every page route acts on the signed-in human's own pile and nobody else's — `src/Sections/Humans.Notifications/Controllers/NotificationsController.cs:24`.
- Badge counts are counted from committed recipient rows on every read, never cached — `src/Sections/Humans.Notifications/Data/NotificationRepository.cs:281`.
- The purge deletes resolved rows past 7 days, unresolved informational rows past 30 days and
  unresolved rows of retired sources, and nothing else — `src/Sections/Humans.Notifications/Services/NotificationInboxService.cs:163`.
- Meter counts are never stored in a table: the consent-review, deletion, sync and ticket counts share a
  two-minute memory cache; team, camp and board-vote counts come live or from their owner's own cache — `src/Sections/Humans.Notifications/Services/NotificationMeterProvider.cs:196`.
- The machine read never mutates: it calls only the inbox's read and the meter provider — `src/Sections/Humans.Notifications/Services/NotificationInboxRead.cs:16`.

## 5. Seams

Specified or known, not built; reserved, not ranked:

- **The Notification Board redesign** (`Docs/features/notification-board.md`, approved) replaces
  the stored inbox with an in-memory board of section-published entries (the `NotificationSource`
  enum it set out to retire is already gone: sections declare `NotificationSourceDefinition`s). Until
  its phase 4 lands, `notification-inbox.md` describes what runs.
- **Role fan-out takes no `sourceKey`**, so `ResolveBySourceKeyAsync` can never clear it.
- **Dispatch is described as fire-and-forget but is not wrapped**; an emit failure reaches the
  caller.

## 6. Deliberately not done

- **No caching decorator.** Inbox and badge counts read current rows; meter counts retain their own short-TTL caches.
- **No real-time push.** Badge counts refresh on navigation.
- **No stored meter.** A slow count is the owning section's to fix with a narrow read.
- **No `GroupKey`.** Shared-versus-individual is decided by which dispatch method is called.
- **Digests stay email.** Summaries are not work items.

## Load-bearing weirdness

- **`NotificationEmitter` is its own type.** `NotificationService` injects
  `IRoleAssignmentService`, and `RoleAssignmentService` emits; it injects the narrow
  `INotificationEmitter`, whose implementation has no edge back, so the graph cannot close.
  `NotificationService.SendAsync` delegates to the emitter to keep one copy of that path.
- **`NotificationRecipient.UserId` is init-only**, so the account-merge fold is
  remove-then-add rather than an update.
- **No FK and no navigation on `ResolvedByUserId` or `NotificationRecipient.UserId`**
  (nobodies-collective/Humans#992, nobodies-collective/Humans#996); names render through the
  shared Human component from the stored id.
- **Retired sources stay declared** (`Retired: true` on the owning section's definition): nothing
  emits them, the purge deletes their unresolved rows, and the inbox's approvals filter still
  names them while any remain.
- **The repository is a Singleton over `IDbContextFactory`** so Singleton-lifetime callers can
  inject it.

## History

| Run | Date | Headline | PR |
|-----|------|----------|----|
| 1 | 2026-08-26 | DI-cycle story corrected; doc names brought in line with the code | peterdrier/Humans#1527 |
| 2 | 2026-09-30 | One row shape; inbox reads stop loading co-recipients | peterdrier/Humans#1866 |
| 3 | 2026-11-03 | One outcome shape per transition; docs stop listing other sections' sources | peterdrier/Humans#2254 |
