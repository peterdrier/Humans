<!-- freshness:triggers
  src/Sections/Humans.Notifications/**
  src/Sections/Humans.Notifications.Contracts/**
  tests/Humans.Notifications.Tests/**
-->

# Notifications — target shape

Derived fresh each section-doctor run, before any scan. History at the bottom.

## 1. What the section does

Every other part of the system, when something happens a human should know about, hands
Notifications a sentence and a list of people. Notifications keeps that sentence until the
people have seen it and — where it represents work — until someone has done the work. It
shows each human their own pile at `/Notifications` and in the bell at the top of every page,
lets them clear items off it, and throws away what has aged out.

Alongside the pile it shows a second thing that looks the same but isn't: a computed count of
work waiting in someone else's queue ("11 consent reviews pending"). Those counts are asked
of the section that owns the work rather than kept in a column here, so they cannot drift the
way a stored counter can — a stale one is at most two minutes behind, because the answers are
held in a short-TTL memory cache that writes elsewhere evict. That bound is the whole
guarantee: not "always current", but "never wrong for longer than the cache lives".

Two rules give the section its character. **Seeing is personal, doing is shared** — each
recipient has their own read state, but when any one of them handles an item it is handled
for everyone it was sent to. And **a human can turn off the chatter but not the work** — a
notification that is merely news respects the recipient's per-category preference and can be
dismissed; one that asks for something goes through regardless and can only be cleared by
doing the thing.

## 2. The shapes

| # | Question a caller is asking | Surface answering it |
|---|---|---|
| 1 | "Tell these humans something happened." | `INotificationEmitter.SendAsync` (explicit recipients) · `INotificationService.SendToRoleAsync` (everyone holding a role) |
| 2 | "That condition is fixed — clear the alerts about it." | `INotificationAutoResolve.ResolveBySourceAsync` (one human, one source) · `ResolveBySourceKeyAsync` (every recipient, one source entity) |
| 3 | "What is on my pile?" | `GET /Notifications` · `GET /Notifications/Popup` · `NotificationBell` chrome component |
| 4 | "I have dealt with this." | `Resolve` · `Dismiss` · `MarkRead` · `MarkAllRead` · `BulkResolve` · `BulkDismiss` · `ClickThrough` |
| 5 | "How much work is waiting for someone like me?" | the meters, computed per-render by `NotificationMeterProvider` |
| 6 | "Throw away what has aged out." | `INotificationRetention.PurgeExpiredAsync`, driven nightly by `CleanupNotificationsJob` |
| 7 | "This human's badge counts are wrong now." | `INotificationService.InvalidateBadgeCachesForUsers` |
| 8 | "Give me / erase everything you hold about this human." | `IUserDataContributor` (export + erasure) |
| 9 | "These two accounts are one human — fold them." | `IUserMerge.ReassignAsync` |

Shapes 1–2 and 6–9 are the cross-section contract; 3–5 are the section's own pages and have
no consumer outside it.

Shape 5 is the odd one out and deliberately so: it is the only shape with no row in the
section's own tables. It sits next to shapes 3–4 on the page because a human reading their
pile does not care which of the two a line came from.

## 3. Structure

The layout those shapes imply, written fresh:

- **One contracts leaf** carrying exactly shapes 1, 2, 6 and 7 plus the enums their signatures
  name. Shape 7 belongs here for the same reason as the rest — the account merge in Users has
  to evict both folded accounts' badges after it commits, and that is a cross-section call.
  Everything else stays inside the section.
- **One dispatch path.** Building a notification and its recipient rows, applying the
  preference filter and evicting the affected badge caches is one piece of logic, whether the
  recipients arrived as a list or as a role name. The role case adds one step in front —
  turn a role name into user ids — and one difference behind: role fan-out makes a single
  shared row, an explicit list makes one row each.
- **One inbox service** owning shapes 3, 4, 6, 8: read models, per-row state transitions,
  the retention cutoffs, the GDPR contribution. Its badge count is the only cached read.
- **One meter provider** owning shape 5, reaching every count through the owning section's
  read interface and never through a table.
- **One repository** as the only thing that touches `notifications` /
  `notification_recipients`.
- **One controller** that parses, calls, and formats — carrying no rule of its own, including
  no copy of a rule the service already applies.
- **One view model per page**, and one row shape between service and view rather than two
  that differ in a default value.

## 4. Invariants

- A table row is written only through `INotificationRepository`; nothing else in the solution
  touches `notifications` or `notification_recipients` (HUM0025).
- Meters are computed, never stored. No `meter_counts` table, ever.
- Read state is per-recipient; resolution is shared across every recipient of a notification.
- `Informational` obeys the recipient's `InboxEnabled` preference for the source's
  `MessageCategory`; `Actionable` ignores it.
- `Actionable` can be resolved, never dismissed — on the single and the bulk path alike.
- A human who is not a recipient of a notification cannot read, resolve, dismiss, mark-read
  or click through it. Whether the refusal distinguishes "not yours" from "does not exist"
  follows from how the route looks the row up, and today the two pairs differ: `Resolve` and
  `Dismiss` load the notification and so can return `Forbidden`; `MarkRead` and `ClickThrough`
  query the `(NotificationId, UserId)` row and so can only return `NotFound`. Either is
  defensible — the id is an unguessable Guid, so there is nothing to enumerate — but the split
  is an accident of two lookup shapes, not a decision, and one of the two should win.
- A human cannot see another human's pile, badge count, or unread total.
- Every source has a deliberate `MessageCategory`, not one arrived at by falling through a
  default arm.
- An emit with no surviving recipients writes nothing and logs why.
- Every mutation made on a human's behalf evicts the badge cache of every user it affected.
  The nightly purge is the one gap: `PurgeExpiredAsync` deletes through repository methods
  that return counts and nothing else, so a recipient whose unresolved informational
  row was just deleted can carry a stale unread badge for the two-minute TTL. Either the purge
  reports who it touched, or the exception gets stated on purpose.
- The nightly purge deletes resolved rows past 7 days, unresolved informational rows past
  30 days, and unresolved rows of retired sources — and never an unresolved actionable row of
  a live source.
- Every string a human reads on `/Notifications` comes from the section's resx set, in all six
  cultures — the page is not admin-side, so the `/Admin/*` exemption does not reach it. The
  meter titles are English literals in `NotificationMeterProvider` today; that is the gap this
  target names, not a second rule.

## 5. Seams

Specified-but-unbuilt; reserved, not ranked, not built here:

- **`SendToRoleAsync` cannot carry a `sourceKey`.** `ResolveBySourceKeyAsync` therefore can
  never clear a role fan-out — the shape-2 answer only reaches shape-1's explicit-recipient
  half. No caller needs it today; the asymmetry is a seam, not a defect.
- **A stated fire-and-forget contract that dispatch does not implement.** Callers are told to
  treat dispatch as fire-and-forget, but no dispatch method is `try`/`catch`-wrapped, so an
  emit failure propagates into the caller's write path. Every call site is currently expected
  to wrap it itself.

## 6. Deliberately not done

- **No caching decorator on the dispatch or inbox service.** Both cache one thing internally
  and evict it in-band on every write; a decorator would sit between the service and its own
  invalidation.
- **No real-time push.** A small user base and a 2-minute badge cache do not buy a socket.
- **No stored meter.** A count that is too slow to compute is the owning section's problem to
  fix with a narrow count method, not this section's to denormalise.
- **No `GroupKey` on a notification.** Whether recipients share one row or get one each is
  decided at the call site by which method is called, and that is the whole mechanism.
- **Daily digests stay email.** They are summaries, not work items; they do not fit
  resolve/dismiss.

## Load-bearing weirdness

- **`NotificationEmitter` is a separate type from `NotificationService`, not a base class of
  it.** The narrow `INotificationEmitter` is what sections that Notifications itself calls
  into may inject, so the graph cannot close on itself. `NotificationService.SendAsync`
  delegating to the emitter — rather than sharing a helper — is what keeps that one piece of
  logic in one place across both types.
- **`NotificationRecipient.UserId` is init-only**, which is why the account-merge fold is
  implemented as remove-then-add rather than an update.
- **`Notification.ResolvedByUserId` and `NotificationRecipient.UserId` carry no FK and no nav
  property** (nobodies-collective/Humans#992, #996). Display names are stitched in memory from
  `IUserServiceRead`. This is the rule for every cross-domain id here, not an oversight.
- **`ApplicationSubmitted` and `ConsentReviewNeeded` are retired sources**: no new rows are
  emitted, historical unresolved rows have no resolution path, and the purge deletes them
  outright. The inbox's "Approvals" filter still names them so the historical rows remain
  findable while they last.
- **The repository is a Singleton over `IDbContextFactory`**, not a Scoped service over a
  Scoped context — that is what lets the Singleton-ish call sites inject it directly.

## History

| Run | Date | Reforge (surface / loc / cogP95 / cogMax) | PR |
|-----|------|-------------------------------------------|----|
| 1 | 2026-08-26 | 274 / 2381 / 7 / 25 | peterdrier/Humans#1527 |
