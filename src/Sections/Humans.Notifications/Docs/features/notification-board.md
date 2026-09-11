<!-- freshness:triggers
  src/Sections/Humans.Notifications/**
  src/Sections/Humans.Notifications.Contracts/**
-->
<!-- freshness:flag-on-change
  Board contract, entry shape, audience labels, rebuild/reconcile semantics, admin view routes, or the per-source migration table may have shifted. This spec supersedes notification-inbox.md once phase 4 lands.
-->

# Notification Board

**Status:** Approved design (Peter, 2026-09-09). Supersedes [`notification-inbox.md`](notification-inbox.md) when phase 4 lands; until then that document describes what runs.

## Business Context

The inbox today is a stored copy of state other sections already own. Nine "actionable" sources mirror a pending join request, a coverage gap, an open issue, an expiring term, a suspension, a failed sync, a pending re-consent. Only three ever auto-resolve; the rest sit open forever. Twenty-two "informational" sources are echo: news that is already in the audit log, and in ten cases already an email. Meters sit beside the rows as a second, computed mechanism with hardcoded role checks and English titles. The result is a bell that is busy and noisy enough not to be useful.

The decisions that reshape it:

- **A notification is something I need to do now.** Nothing else. No news, no "Bob joined Geeks". Whoever wants that reads the audit log on the team page.
- **Deciding it is the only way to close it.** No dismiss, no snooze, no shared "handled by" state. When the underlying condition is gone, the item is gone.
- **Nothing durable.** The section owns no tables. State lives in RAM for the lifetime of the process and is rebuilt from the owning sections.
- **Notifications holds current state for every user at runtime.** A page render is a dictionary read, not a fan-out. Sections push facts to the board as they change them.
- **Audit is for things a human did.** Board mutations are not audited. The one human action on the admin page (rebuild now) is.

## Concepts

- A **Notification Entry** is one open item of work for one or more humans, published by the section that owns the underlying state and retracted by that same section when the state changes. It has a section-owned idempotent **Key**.
- An **Audience** is the label of who an entry was published for: `User`, `Role:<name>`, `Team:<teamId>`, `TeamCoordinators:<teamId>`, `CampLeads:<seasonId>`. Entity audiences carry the entity's Guid, so the admin view can name them through `IEntityNameContributor`; role audiences carry the role name, which is already a label. The publishing section resolves the label to user ids itself; the board records both. An entry may be published to several audiences at once. New kinds are a new label, not a change to Notifications.
- An **Audience owner** is the section that owns an audience's membership: Auth for `Role`, Teams for `Team` and `TeamCoordinators`, Camps for `CampLeads`. It tells the board when that membership changes; it never touches another section's entries.
- The **Board** is the process-wide in-memory map of entries, indexed by user, by audience, and by source. It is a materialized view of the sections' live predicates: incremental publish/retract is the fast path, the predicate is the truth, reconcile bounds the drift.
- A **Publisher** is a section's implementation of the fan-out contract that can enumerate every entry it currently owes, for everyone. Used at startup and by reconcile.
- A **Source** is `<section>/<kind>` in lower-kebab (`teams/join-request`, `issues/assigned`), owned by the publisher. It replaces the `NotificationSource` enum.

## Contract

Lives in `Humans.Notifications.Contracts`. Four inbound calls and one fan-out. Everything else in the section stays internal.

```csharp
public interface INotificationBoard
{
    /// Publishes or replaces the entry with this key for every listed audience.
    /// A re-publish keeps the original PublishedAt. Rejected and logged unless
    /// <paramref name="section"/> is a registered publisher's Section and the key starts with "<section>/".
    void Publish(string section, NotificationEntry entry, IReadOnlyList<NotificationRecipients> recipients);

    /// Removes the entry with this key for every audience. No-op when absent. Same ownership check.
    void Retract(string section, string key);

    /// Removes every entry of that section whose key starts with the prefix. For "this team is gone" cases.
    void RetractByPrefix(string section, string keyPrefix);

    /// Told by an audience owner that the audience's membership changed. The board schedules a
    /// debounced reconcile; user-set changes on entries carrying this audience are counted as
    /// audience refresh, not drift.
    void RefreshAudience(NotificationAudience audience);
}

public interface INotificationPublisher : IFanout
{
    /// The section name this publisher owns keys for; "<Section>/" is the key prefix. One publisher per section.
    string Section { get; }

    /// Publishes every entry this section currently owes, for everyone. Idempotent.
    /// Called at startup and by reconcile; never by the section itself.
    Task PublishAllAsync(INotificationBoard board, CancellationToken ct);
}

public sealed record NotificationEntry(
    string Key,               // "<section>/<kind>:<entity id>"; section-owned, idempotent
    string Source,            // "<section>/<kind>"
    Type ResourceType,        // the section's <Section>Resource marker; rendered in the viewer's culture
    string TextKey,           // resx key for the one-line text
    object[] TextArgs,        // culture-neutral values: names, ints, Instant/LocalDate; formatted at render
    string ActionUrl,         // where deciding it happens; required, always local
    TileSeverity Severity = TileSeverity.Normal,
    string? DescriptionKey = null,
    object[]? DescriptionArgs = null);

public sealed record NotificationRecipients(NotificationAudience Audience, IReadOnlyCollection<Guid> UserIds);

public sealed record NotificationAudience(
    NotificationAudienceKind Kind,
    Guid? EntityId = null,     // Team, TeamCoordinators, CampLeads: the entity's id, resolvable by IEntityNameContributor
    string? RoleName = null);  // Role: the RoleNames constant
```

Rules:

- Entries carry resource keys, not rendered text. The board holds items for every user and users have different cultures; the bell renders each entry in the viewer's culture through `IStringLocalizerFactory.Create(entry.ResourceType)`. Format args stay culture-neutral (names, counts, NodaTime values) and are formatted at render in the viewer's culture; a section never pre-formats a date or number, because a startup or reconcile publish has no viewer and a write-path publish only has the actor's culture.
- Audience labels carry no display text. Entity audiences carry a Guid the admin view resolves through `IEntityNameContributor.ResolveNamesAsync(IReadOnlyCollection<Guid>)`, the same call that resolves user ids; Camps contributes season names if it does not already. Role audiences render the role name directly.
- `Publish`, `Retract`, `RetractByPrefix` and `RefreshAudience` are synchronous, in-memory, and never throw to the caller. A malformed entry, an unknown section, or a key outside the section's prefix is logged and dropped.
- A section calls `Publish` and `Retract` in the write path that changes the state, in the same place it writes audit. The same rule that puts audit after the business save applies.
- Ownership is the key prefix. The `section` argument names a registered publisher and must match the key prefix, so a typo or collision cannot remove another section's entries by accident. A caller lying about its section is not a threat model inside one process; reconcile re-adds anything wrongly removed and counts it as drift. If it ever bites, an analyzer on the `section` literal is the fix, not a runtime identity scheme.
- When an audience's membership changes, its owner calls `RefreshAudience`. Auth after a role assignment starts or ends, Teams after a coordinator joins or leaves, Camps after a lead changes. No section re-publishes another section's entries; the board runs the publishers.

## The board

Singleton in `Humans.Notifications`, internal. `ConcurrentDictionary<string, BoardItem>` keyed by entry key, plus three derived indexes maintained under the same lock: by user id, by audience, by source. `BoardItem` = entry + recipients (audience → user ids) + `PublishedAt` + `LastPublishedAt` + `MutationSeq`. The board keeps a monotonic mutation sequence; every publish and retract stamps its key, and a retract leaves a tombstone with its sequence until the next reconcile completes.

Read surface (internal, consumed by the section's own views and by the admin page):

- `ForUser(userId)` → the user's open items, ordered by severity then age.
- `CountForUser(userId)` → the badge.
- `ByAudience()`, `BySource()`, `Health()` → the admin page.

**Startup.** A hosted service registered by the section's `Section.Register` runs after all sections are registered and performs a reconcile against the empty board: the same sequence-aware, per-section diff described below, so a write-path publish or retract that lands while a publisher is still reading wins over that publisher's older snapshot exactly as it does during a scheduled reconcile. Publishers run in parallel and fail soft (a throwing publisher is logged; its entries are absent until the next reconcile). The bell renders empty until the rebuild completes; requests are never blocked on it. `Health()` exposes `RebuiltAt`.

**Reconcile.** A Hangfire job, `notifications-reconcile`, on a fixed interval (implementer's call; start at 15 minutes), also scheduled a few seconds after any `RefreshAudience` call. It never swaps the board. It records the current mutation sequence, runs every publisher into a scratch board, then applies a per-section diff to the live board under the lock:

- A publisher that threw is skipped: its section's live entries are left untouched and it is counted in `publisher_failures`. Only sections whose publisher succeeded are diffed.
- A key the live path mutated after the recorded sequence (published, re-published, or retracted with a tombstone) is excluded from the diff: the write path is newer than the publisher's snapshot and wins. Tombstones older than the recorded sequence are cleared when the reconcile completes.
- For the rest: added, removed, and user-set changes are applied, with `PublishedAt` preserved for keys that survive. Each is counted. An addition, removal, or user-set change on an entry carrying an audience with a pending `RefreshAudience` is counted as `audience_refresh` (a new Board member gains per-member vote entries, a departed one loses them); every other count is drift, and drift is a bug in a section's write path, not something to tune away.

**Rebuild now.** Same code as reconcile, triggered from the admin page. Audited as an admin action.

## Surfaces

All user-facing strings on the bell, popup and `/Notifications` come from `NotificationsResource` in all six cultures. Entry text comes from the publishing section's resource set.

| Verb | Route | Purpose | Who |
|------|-------|---------|-----|
| GET | `/Notifications` | The uncondensed list: every open item for the signed-in human, grouped by source, with age | any authenticated human |
| GET | `/Notifications/Popup` | Bell dropdown partial: same items, condensed | any authenticated human |
| GET | `/Notifications/Admin` | The admin view (below) | Admin |
| POST | `/Notifications/Admin/Rebuild` | Rebuild now; antiforgery; audited | Admin |

The bell is the section's own view component, rendered by Shell by name in the header chrome slot as today. The badge is the count of open items. There is no unread state: without rows there is no "new since you last looked", and that is accepted.

The dashboard things-to-do list (`ThingsToDoViewComponent` in Shell, `ISectionThingsToDo` in Base) becomes a second renderer over the board: the three contributors (Users, Governance, Shifts) become publishers with a `User` audience, the component moves into Notifications so there is one aggregator, and `ISectionThingsToDo` is retired. Shell renders it by name as it renders the bell. `IsDone` entries (rendered struck through today) do not exist on the board; done is absent.

No POST routes act on entries. Deciding it happens at `ActionUrl`, in the owning section.

### Admin view, `/Notifications/Admin`

Read-only except Rebuild. Admin-side, so no new resx keys. Display names for user ids and audience ids resolve through the existing `IEntityNameContributor` fan-out.

- **By person.** Pick a human; see their open items with source, audience, age.
- **By audience.** Every role, team, coordinator group and camp-lead group with open items; expand to the items and to who is in the audience.
- **By source.** Count and oldest age per source. This is where "why is nobody handling join requests" shows up.
- **Health.** `RebuiltAt`, last reconcile time, last reconcile diff (added, removed, changed), publisher failures, and the Rebuild button.

Reachable from the admin navigation via `ISectionAdminNav`.

## Metrics

Through `IMeters.Declare`, exported under the existing Humans meter. `IMeters` is name plus `Set(int)` only: no tags, no counters. Per-source series are therefore separate gauges named by source, declared on the first publish of that source (`Declare` is idempotent by name), and there are no rate counters; publish and retract volume is not needed for what these gauges are for. Set on every board mutation and after every reconcile; nothing polls.

| Gauge | Meaning |
|-------|---------|
| `notifications.open.<source>` | open entries for the source |
| `notifications.users_open.<source>` | users with at least one open entry for the source |
| `notifications.oldest_age_seconds.<source>` | age of the oldest open entry for the source |
| `notifications.open_total` | all open entries |
| `notifications.reconcile.added` | entries the last reconcile added (incremental path missed a publish) |
| `notifications.reconcile.removed` | entries the last reconcile removed (incremental path missed a retract) |
| `notifications.reconcile.changed` | user-set corrections not attributable to an audience refresh |
| `notifications.reconcile.audience_refresh` | additions, removals and user-set corrections attributable to a `RefreshAudience` call |
| `notifications.publisher_failures` | publishers that threw on the last rebuild or reconcile |

The three drift gauges (`added`, `removed`, `changed`) are the ones to alert on.

## Per-source migration

Every source today, and what it becomes. Audience is who the entry is published for; the section resolves the ids.

### Becomes a board entry

| Today | Owner | Entry | Audience | Retracted when |
|-------|-------|-------|----------|----------------|
| TeamJoinRequestSubmitted, join-requests meter | Teams | one per pending request | `TeamCoordinators:<teamId>`; a second entry per team for `Role:Admin` when the team has no coordinator | request decided or withdrawn |
| ShiftCoverageGap | Shifts | one per upcoming shift with confirmed < minimum, same predicate `CheckAndNotifyCoverageGapAsync` uses | `TeamCoordinators:<rota's team>` | shift reaches minimum, is cancelled, or starts |
| IssueSubmitted | Issues | one per open unassigned issue | one `Role:<name>` recipient set per routed role from `IssueSectionRouting.RolesFor`, plus `Role:Admin`, all on the one entry | assigned or terminal |
| IssueAssigned | Issues | one per open issue assigned to you | `User` | reassigned or terminal |
| TermRenewalReminder | Governance | one per term expiring within the job's window, excluding terms with a pending renewal application for the same tier (the predicate `TermRenewalReminderJob` uses) | `User` | renewed, expired, or a renewal application submitted |
| Board-vote meter | Governance | one per (application, board member) still to vote, since "awaiting *your* vote" is per person | `Role:Board`, with a single-member recipient set on each entry, so a Board membership refresh attributes to the role | voted or decided |
| AccessSuspended | Users | one while suspended | `User` | unsuspended |
| ReConsentRequired, LegalDocumentPublished, consents things-to-do | Governance | one per required consent outstanding | `User` | consent signed |
| Consent-review meter, onboarding-pending meter | Users | one per profile awaiting review | `Role:ConsentCoordinator`, `Role:Board`, `Role:VolunteerCoordinator` | reviewed |
| Pending-deletions meter | Users | one per pending deletion | `Role:Admin` | deletion executed or cancelled |
| SyncError, failed-sync meter | GoogleIntegration | one per failed sync event | `Role:Admin` | retried or discarded |
| Ticket-sync meter | Tickets | one while in error state | `Role:Admin` | sync recovers |
| Camp-lead meter | Camps | one per pending requester on a season you lead | `CampLeads:<seasonId>` | request decided or season closed |
| Shift-info things-to-do | Shifts | one while a signed-up volunteer's shift profile is empty | `User` | profile filled |
| Profile things-to-do | Users | one while profile completion is below the contributor's 80% threshold | `User` | threshold reached |
| Dietary/medical things-to-do | Users | one while `DietaryPreference` is empty; text varies by whether a qualifying cantina signup exists, as today | `User` | preference set |
| RideshareInterestReceived | Rideshare | one per pending interest awaiting your decision on your trip or request | `User` | accepted, declined, or withdrawn |
| AssemblyVoteOpened | Governance | one per open assembly vote you are on the roster for and have not voted in | `User` | ballot cast, or the vote closes or is cancelled |

Every-active-user fan-out (`LegalDocumentSyncService.TryFanoutAsync`) is deleted; the consents entry covers it per user.

### Becomes an email, in the owning section, separate PR each

These have no channel once the in-app row goes. The list is a recommendation Peter has not yet ruled on; whichever rows survive land as additive email PRs **before** phase 4, so no deployment leaves an event with no channel at all. A row Peter strikes moves to the deleted list.

| Today | Trigger, recipient | Email category |
|-------|--------------------|----------------|
| TeamMemberRemoved | coordinator or admin removes you from a team | TeamUpdates |
| TeamJoinRequestDecided, reject branch | coordinator rejects your join request (approve already emails) | TeamUpdates |
| RoleAssignmentChanged | governance role assigned to or ended for you | Governance |
| CampMembershipApproved, CampMembershipRejected | camp lead decides your request | TeamUpdates |
| CampMembershipSeasonClosed | season withdrawn while your request pends | TeamUpdates |
| ShiftAssigned | coordinator puts you on a shift you did not pick | VolunteerUpdates |
| IssueStatusChanged | your issue reaches a terminal status (reporter only; intermediate moves stay silent) | System |
| RideshareInterestAccepted, RideshareInterestDeclined | the trip owner or requester decides your interest | VolunteerUpdates |

### Deleted with no replacement

TeamMemberAdded (both branches; the member branch already emails), TeamJoinRequestDecided approve branch (already emails), ApplicationApproved, ApplicationRejected, ProfileRejected, FeedbackResponse, WorkspaceCredentialsReady, CampaignReceived (all already email), IssueComment (assignee sees it on the issue; the admin-comment email to the reporter stays), ShiftSignupChange (a coordinator's news about a signup; the consequence that matters is the coverage-gap entry), CampRoleAssigned, GoogleDriftDetected, FacilitatedMessageReceived (already emails), the three dead sources ConsentReviewNeeded, ApplicationSubmitted, VolunteerApproved, and the Users consent-check things-to-do entry (a status with no action for the member; the coordinator side is the consent-review row above).

Every value of `NotificationSource` at the time of writing (0 to 36, 17 unused) appears in exactly one of the three tables above. A source added between now and phase 4 gets a row before the switch lands.

## What is deleted

- Tables `notifications` and `notification_recipients`, `NotificationsDbContext`, its migrations and factory. The drop is its own PR after prod verification, per [`no-drops-until-prod-verified`](../../../../../memory/architecture/no-drops-until-prod-verified.md).
- `INotificationEmitter`, `INotificationService`, `INotificationAutoResolve`, `INotificationRetention`, `NotificationSource`, `NotificationClass`, `NotificationPriority`, `NotificationSourceMapping`.
- `NotificationService`, `NotificationEmitter`, `NotificationInboxService`, `NotificationMeterProvider`, `NotificationRepository`, `CleanupNotificationsJob`, the `IUserMerge` and `IUserDataContributor` participation (the board holds nothing to export; a merged or deleted user's entries go with the owning section's state and the next reconcile).
- Controller routes Resolve, Dismiss, MarkRead, MarkAllRead, BulkResolve, BulkDismiss, ClickThrough, and the inbox views behind them.
- `INotificationMeterCacheInvalidator` and its two call sites; `CacheKeys.NotificationBadgeCounts`, `NotificationMeters`, `CampLeadJoinRequestsBadge`.
- The 35 emitting call sites across Teams, Shifts, Issues, Camps, Auth, Governance, Feedback, Onboarding, Users, Campaigns, GoogleIntegration, Consent, Rideshare; the three auto-resolve call sites.
- `ISectionThingsToDo` and its three implementations, `ThingsToDoViewComponent` in Shell.
- Users' `CommunicationPreference.InboxEnabled`: property override first, column drop in the same drop PR as the tables, per [`no-column-drops-for-decoupling`](../../../../../memory/architecture/no-column-drops-for-decoupling.md).

## Authorization

- A human sees only their own entries. No endpoint takes a user id.
- `/Notifications/Admin` and `/Notifications/Admin/Rebuild` require Admin; anyone else is `Forbid`.
- `ActionUrl` is local-URL-checked at render, as `ClickThrough` checks it today.

## GDPR

The board holds user ids and entry args (which may include display names) in RAM only. Nothing is exported: every entry derives from state its owning section already exports. On account deletion the owning sections' state changes retract the entries; the next reconcile clears anything left. On account merge the same applies, and `RetractByPrefix` is not involved.

## Phases

1. **Board core** (Notifications only, no user-visible change). Contracts, board, startup rebuild, reconcile job, gauges, `/Notifications/Admin` with Rebuild, `ISectionAdminNav` entry. Tests under `tests/Humans.Notifications.Tests`: publish/retract idempotence, `PublishedAt` survival, multi-audience publish and by-audience index, ownership rejection, reconcile diff, a live mutation during reconcile winning over the snapshot, a failed publisher leaving its entries alone, audience-refresh classification, per-user isolation, admin deny path.
2. **Publishers**, one PR per section, additive: implement `INotificationPublisher`, add `Publish`/`Retract` at the write sites, keep the old emits. The admin page is the verification surface: after each section lands, its entries appear there and the reconcile gauges stay at zero.
3. **Emails**, one additive PR each in the owning section, for whichever rows of the email table Peter keeps. Must be deployed before phase 4 so no event loses its only channel.
4. **The switch** (one PR, mostly deletion): bell, popup, `/Notifications` and the dashboard list read from the board; everything under "What is deleted" except the tables and the column goes; `Notifications.md` rewritten to this shape; `notification-inbox.md` deleted. Gated on phases 2 and 3 being complete for every section.
5. **Drop PR**: tables, context, migrations, `InboxEnabled` column. Needs Peter's per-case approval with evidence the tables hold nothing in use.

## Implementer's calls

- Key naming beyond the `<source>:<id>` convention.
- Reconcile interval.
- Whether `NotificationAudienceKind` is an enum or the label string; the admin view only groups by it.
- Ordering and grouping on `/Notifications` and in the popup.
- The Board-vote entry is one per (application, member) carrying the `Role:Board` audience with a one-member recipient set, because the board never filters at read time and a Board membership change must attribute to the role. Revisit only if it makes Governance's write path noisy.

## Rules this touches

- [`crosscut-purity`](../../../../../memory/architecture/crosscut-purity.md): the board is inbound only. Notifications calls no section; the audience label is recorded, never resolved here. `SendToRoleAsync` calling Auth goes away with it.
- [`no-admin-url-section`](../../../../../memory/architecture/no-admin-url-section.md): the admin view lives at `/Notifications/Admin`.
- [`localization-admin-exempt`](../../../../../memory/code/localization-admin-exempt.md): `/Notifications/Admin` is admin-side; the bell, popup and `/Notifications` are not.
- [`no-new-displayname-fields`](../../../../../memory/code/no-new-displayname-fields.md): no `DisplayName` anywhere on the contract; audiences carry ids and names are resolved at render.
- [`reuse-first-change-discipline`](../../../../../memory/process/reuse-first-change-discipline.md): `IMeters`, `IEntityNameContributor`, `ISectionAdminNav`, `ISectionChrome`, `IFanout` reused; `ISectionThingsToDo` retired rather than kept beside the board. New public surface is the five contract types above, replacing seven.
