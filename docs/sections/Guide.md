# Guide — Section Invariants

## Concepts

- A **guide page** is one markdown file under `docs/guide/` in the Humans
  repo, rendered at `/Guide/<FileStem>`.
- A **role-scoped block** is a `## As a …` heading and the content under
  it, wrapped in `<div data-guide-role="…" data-guide-roles="…">` by the
  renderer and optionally stripped at request time by `GuideFilter`.
- A **parenthetical** is text in parens after `## As a …` (e.g. `## As a
  Board member / Admin (Teams Admin)`) that specifies which domain admin
  role sees that block.

## Actors & Roles

| Role | Visibility |
|------|------------|
| Anonymous | View Volunteer-scoped blocks only; cannot trigger refresh |
| Any authenticated human | View Volunteer-scoped content |
| Team coordinator (TeamMember.Role == Coordinator) | Additionally view Coordinator-scoped blocks |
| Domain admin (*Admin or *Coordinator system role named in a parenthetical) | Additionally view blocks whose parenthetical names their role, on those specific files |
| Admin, Board | View all blocks on all pages |
| Admin | Trigger `POST /Guide/Refresh` |

## Invariants

- All content above the first `## As a …` heading in a file is always
  visible regardless of role.
- All content at or below a non-`As a …` `## ` heading (e.g.
  `## Related sections`) is always visible.
- Anonymous users only see Volunteer-scoped blocks. They never see
  Coordinator or Board/Admin blocks.
- Guide content is the 17 files in `docs/guide/` on the current branch;
  nothing is authored in-app.
- Cache key is `guide:<FileStem>`. TTL is sliding, configured via
  `Guide:CacheTtlHours` (default 6).
- Only the `GuideContentService` reads or writes the `guide:*` cache
  entries. No other service touches guide content.

## Triggers

- First `GET /Guide/*` after cold start → full refresh of all 17 cache
  entries.
- `POST /Guide/Refresh` (Admin) → clears and repopulates all entries.
- GitHub fetch failure on warm cache → stale content served; warning
  logged; TTL preserved.
- GitHub fetch failure on cold cache → `GuideContentUnavailableException`
  bubbles; controller renders `Unavailable.cshtml`.

## Cross-Section Dependencies

- Reads `TeamMember.Role` to resolve the `IsTeamCoordinator` flag for the
  current user (see `Teams` section).
- Reads `User.IsInRole` claims for the `SystemRoles` set (see `Admin`
  section for role assignments).
