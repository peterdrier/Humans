# Settings — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SettingsController` (`/Settings`) | Class | any authenticated member | `[Authorize]` — hosts the tabs sections contribute via `ISectionSettings`, peterdrier/Humans#1628 |
| `SettingsAdminController` POST (`/Settings/Admin`) | Class | `Admin` | `PolicyNames.AdminOnly` — the app-wide event settings write, incl. minting a new cycle, nobodies-collective/Humans#1104 and nobodies-collective/Humans#1631; no GET remains |

The Event tab's editable/read-only split is not a route gate: `EventSettingsTabViewComponent`
calls `IAuthorizationService.AuthorizeAsync(user, null, PolicyNames.AdminOnly)` and renders
the shared form only when it succeeds, else a read-only view with no `<form>` (peterdrier/Humans#1628).

`/Settings` is reachable from the signed-in user menu (`SectionChrome` contributes it to the
`user-menu` chrome slot, unconditionally — same gate as the page's own `[Authorize]`).
