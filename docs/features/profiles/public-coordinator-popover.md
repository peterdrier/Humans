<!-- freshness:triggers
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Views/Shared/_HumanPopoverPublic.cshtml
  src/Humans.Web/Models/PublicPopoverViewModel.cs
  src/Humans.Web/ViewComponents/HumanViewComponent.cs
  src/Humans.Web/Views/Shared/Components/Human/Default.cshtml
  src/Humans.Web/Views/Team/Details.cshtml
  src/Humans.Web/wwwroot/js/site.js
-->
<!-- freshness:flag-on-change
  Public popover gating (active coordinator on IsPublicPage && ShowCoordinatorsOnPublicPage team), reduced-partial fields, or the AllowAnonymous endpoint contract — review when any of these shift.
-->

# Public Coordinator Popover

## Business Context

The public team page (`/Team/{slug}`) renders coordinator cards when the team has `IsPublicPage && ShowCoordinatorsOnPublicPage` enabled, so anonymous visitors can see who the coordinator faces are without logging in. The authenticated `<vc:human>` hover popover surfaces a fuller card (city/country, full team list, languages, etc.) but is `[Authorize]`-gated by the existing `/Profile/{id}/Popover` action. Anonymous viewers got no hover affordance at all — only the static card.

This feature adds a reduced popover for anonymous viewers that mirrors the data already visible on the public team page (avatar, BurnerName, public coordinator role labels) and explicitly omits everything else.

Fixes [nobodies-collective/Humans#771](https://github.com/nobodies-collective/Humans/issues/771).

## User Stories

### US-PCP.1: Anonymous hover popover on public team page
**As an** anonymous visitor on `/Team/{slug}`
**I want to** hover the coordinator card and see a small popover with the coordinator's name, avatar, and role
**So that** I can recognize who is responsible without having to sign in

**Acceptance Criteria:**
- Anonymous `GET /Profile/{id}/PublicPopover` returns 200 with the reduced partial when the target user is an active coordinator on a team with `IsPublicPage && ShowCoordinatorsOnPublicPage`.
- Returns 404 in every other case so anonymous probes cannot enumerate users.
- Reduced partial surfaces only: avatar (72px), BurnerName, role labels formatted as `"Coordinator · {TeamName}"`.
- Partial NEVER renders city/country, the full team list, languages, tier badge, or suspended badge.
- On `/Team/{slug}` while logged out, hovering the coordinator card fires the popover.
- 404 from the endpoint disposes the popover silently (no error tooltip).

### US-PCP.2: Authenticated popover unchanged
**As an** authenticated viewer
**I want to** keep seeing the full `_HumanPopover` content
**So that** the existing hover behavior is preserved

**Acceptance Criteria:**
- Authenticated `GET /Profile/{id}/Popover` continues to return the existing full partial.
- The 8 existing `link="None"` `<vc:human>` call sites still suppress the popover by default.
- `<vc:human popover-public="true">` opts in to the reduced popover even when `link="None"`.

## Endpoints

| Method | Route | Auth | Returns |
|---|---|---|---|
| `GET` | `/Profile/{id}/PublicPopover` | `[AllowAnonymous]` | `_HumanPopoverPublic` partial (200) when active public coordinator; 404 otherwise |

Allowlisted in `EndpointAuthorizationTests` alongside the existing `[AllowAnonymous]` `/Profile/{id}/Picture` endpoint (PR #649 pattern).

## Data Model

No schema changes. The public-coordinator gate filters the existing `ITeamServiceRead.GetTeamsAsync()` projection inline on the controller — mirroring the existing inline `TeamInfo` filter in the authenticated `Popover` action. No new method is added to `ITeamServiceRead` (see [`memory/architecture/interface-method-additions-are-debt.md`](../../../memory/architecture/interface-method-additions-are-debt.md) and `docs/architecture/peters-hard-rules.md` — controllers own filtering).

`PublicPopoverViewModel` (web layer):

| Field | Type | Notes |
|---|---|---|
| `UserId` | `Guid` | Target user |
| `DisplayName` | `string` | BurnerName |
| `RoleLabels` | `IReadOnlyList<string>` | One entry per active public coordinator role, format `"Coordinator · {TeamName}"` |

## View Component Wiring

`HumanViewComponent` gains a `popoverPublic` parameter (and `HumanViewModel.ShowPopoverPublic` flag). When set, `Default.cshtml` emits `data-human-popover-public="true"` on the Card no-link branch and bypasses the usual `link != HumanLink.None` gate. The scope is intentionally limited to the Card no-link branch — that is the only layout the public coordinator card uses today, and other layouts have no public-popover caller. See the `Default.cshtml` comments for the explicit scope contract.

`wwwroot/js/site.js` popover bootstrap delegate matches `[data-human-popover-public]` in addition to `[data-human-popover]` and fetches `/Profile/{id}/PublicPopover`. A 404 response disposes the popover silently.

## Related

- [`docs/sections/Profiles.md`](../../sections/Profiles.md) — Profile section invariants.
- [`docs/sections/Teams.md`](../../sections/Teams.md) — `IsPublicPage` and `ShowCoordinatorsOnPublicPage` flags.
- [`docs/features/profiles/profile-pictures-birthdays.md`](profile-pictures-birthdays.md) — `[AllowAnonymous] /Profile/{id}/Picture` pattern this endpoint mirrors.
