<!-- freshness:triggers
  src/Humans.Web/Controllers/Mailer/MailerAdminController.cs
  src/Humans.Web/Views/Mailer/Admin/Debug.cshtml
  src/Humans.Web/Views/Mailer/Admin/_DebugPager.cshtml
  src/Humans.Web/Views/Mailer/Admin/_DebugSortHeader.cshtml
  src/Humans.Web/Models/Mailer/MailerAudienceDebugViewModel.cs
  src/Humans.Web/Models/Mailer/MailerAudienceDebugSnapshotBuilder.cs
  src/Humans.Application/Services/Mailer/Audiences/TicketNoShiftsAudience.cs
-->

# Mailer Audience Debug Screen

## Business Context

The first run of the `Humans - Ticket no Shifts` audience produced bounceback complaints from people who **did** have shifts. Investigation traced two distinct sources:

1. **Stale snapshot.** Audience sync ran once at list creation; humans who signed up for shifts afterward stayed in the MailerLite group (group state doesn't auto-refresh from Humans).
2. **Wrong email per human.** A human with multiple verified `UserEmail` rows landed in the group under a non-primary email (the "Frank pattern" — `frank@gmail.com` in MailerLite while the current primary is `frank@nobodies.team`).

Before this feature, admins could hit the audience **Sync** button blind and read the resulting banner — no way to inspect *who is supposed to be on a list*, *who is actually on it in MailerLite*, or *what the next sync will change*. As more audiences are added this becomes a recurring need.

## Scope

A per-audience debug screen on the existing Mailer admin section that previews exactly what the next `Sync` would apply, so the admin can spot anomalies before pulling the trigger.

## User Stories

- As an admin, I want to see who Humans thinks should be in audience X versus who's actually in the MailerLite group, so I can confirm the audience compute is correct.
- As an admin, I want to see the to-add and to-remove diff for the next sync without running it, so I can sanity-check the magnitude before applying.
- As an admin, I want the "Frank pattern" (user subscribed under a non-primary email) called out explicitly, so I understand *why* a user appears in both the to-add and to-remove lists.
- As an admin, I want to apply the diff from one button so I don't have to navigate back to the dashboard.

## Route

`GET /Mailer/Admin/Audiences/{key}/Debug` — `AdminOnly`. Same auth as the rest of the section.

## Five Sections

| # | Section | Source |
|---|---|---|
| 1 | **Should be on list** (expected) | `IMailerAudience.ComputeMemberUserIdsAsync` → notification-target email per `UserInfo` |
| 2 | **Currently on list in ML** | Subscribers from `IMailerLiteService.ListSubscribersAsync` whose `GroupIds` contains the audience's group |
| 3 | **To add** | §1 \ §2 by normalized email |
| 4 | **To remove** | §2 \ §1 by normalized email |
| 5 | **Subscribed under non-primary email** (diagnostic) | For each row in §2: if it matches a verified `UserEmail` whose owner's *primary* is a different email, surface the pair side-by-side |

§5 is diagnostic only — there is no separate apply path. When the underlying user belongs in the audience, the regular §3 (add primary) + §4 (remove non-primary) naturally swaps the emails on the next apply.

## Suppressed-status filter

§2 excludes subscribers in `unsubscribed` / `bounced` / `junk` statuses, mirroring `MailerAudienceSyncService.UnsubscribedStatuses`. The two filters are conceptually coupled — if the apply path's filter changes, the debug-screen filter must follow or the preview will lie about what Apply will do.

## Caching

- Audience compute reads cached interfaces only — `IShiftView` + `ITicketQueryService` (both decorated by their caching layers). No DB queries during page render.
- Name/email rendering reads cached `UserInfo` via `IUserService.GetAllUserInfosAsync`. Pinned by `MailerAudienceDebugSnapshotBuilderTests.Build_NoDbQueries_OnlyCachedUserInfoAndMlReads`.
- MailerLite reads are live (we're diffing against the remote we don't own).

## Paging + Sorting

Server-side. Page sizes 20 / 50 / 100 / 200, default 20. Sortable: name, email, "in-ML-since" (§2 only). Default sort: name ascending. State per section is independent (each table has its own `*.page` / `*.size` / `*.sort` / `*.desc` querystring keys).

## Apply Button

Bottom-right of the page; JS `confirm()` with the two counts. POSTs to the existing `/Mailer/Admin/Audiences/{key}/Sync` action — that already does fresh-recompute + diff + apply + audit. Redirects with the existing `TempData["Banner"]` summary.

## Out of Scope

- Section 6 "subscribed via stub-state User" — stubs aren't the current confusion class and would need separate semantics.
- Fixing the root cause behind §5 anomalies (non-deterministic primary picker, `IsPrimary` flag drift). Separate concern; this screen surfaces the anomaly and patches symptoms via the next sync but doesn't prevent recurrence.

## Related

- [`docs/sections/Mailer.md`](../../sections/Mailer.md) — section invariants, including the new route.
- Issue [nobodies-collective/Humans#773](https://github.com/nobodies-collective/Humans/issues/773) — original spec.
