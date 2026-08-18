<!-- freshness:triggers
  src/Sections/Humans.Teams/**
  src/Sections/Humans.Campaigns/Services/CampaignService.cs
  src/Sections/Humans.Users/ViewComponents/ProfileCardViewComponent.cs
-->
<!-- freshness:flag-on-change
  IsHidden visibility filtering touchpoints (directory/detail/profile card/birthday/My Teams/join/admin badge) may have shifted.
-->

# Hidden Teams

## Business Context

Campaigns (code distribution) target teams to determine who receives codes. Some use cases -- like low-income ticket programs -- require grouping users into a team for targeting, but exposing that membership would violate privacy. Hidden teams allow admins to create privacy-sensitive groups without revealing membership to other users.

## Data Model

- `Team.IsHidden` (bool, default `false`) -- when true, the team is invisible to non-admin users.

## Visibility Rules

| Viewer | Can see hidden teams? |
|--------|----------------------|
| Anonymous | No |
| Regular authenticated human | No on **another** human's profile card, team listings, public pages, birthday calendars, or "My Teams". **Yes on their own profile card** — `ProfileCardViewComponent` filters `IsHidden` only when `viewMode == Public`, and a human viewing themselves gets `Self`, so their own hidden-team memberships are listed back to them (see nobodies-collective/Humans#1071) |
| Admin / Board / TeamsAdmin | Yes -- full visibility in team directory, detail pages, admin summary, and "My Teams" |
| Campaigns | Yes -- campaigns target by team ID, unaffected by visibility |

## Touchpoints

- **Team directory** (`GetTeamDirectoryAsync`): hidden teams excluded for non-admin users
- **Team detail page** (`GetTeamDetailAsync`): returns null for hidden teams when viewer is not Admin/Board/TeamsAdmin
- **Profile card** (`ProfileCardViewComponent`): hidden teams filtered alongside the existing Volunteers filter
- **Birthday team names** (`TeamController.Birthdays`): hidden teams excluded via inline filter on `GetTeamsAsync`
- **My Teams** (`GetMyTeamMembershipsAsync`): hidden teams excluded for non-admin users
- **Join flow**: hidden teams return 404 for non-admin users
- **Admin summary**: shows "Hidden" badge on hidden teams
- **Create/Edit team forms**: checkbox toggle for IsHidden

## Related Features

- [Teams](../teams/teams.md) -- base team management
- [Campaigns](../campaigns/campaigns.md) -- code distribution targeting
