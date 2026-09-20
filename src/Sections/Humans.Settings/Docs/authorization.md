# Settings — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SettingsController` (`/Settings`) | Class | any authenticated member | `[Authorize]` — hosts the tabs sections contribute via `ISectionSettings`, #1628 |
| `SettingsAdminController` GET (`/Settings/Admin`) | Action | any authenticated member | `[Authorize(Policy = AdminOnly)]` on the class, but the GET redirects everyone to `/Settings#event` regardless — #1628 |
| `SettingsAdminController` POST (`/Settings/Admin`) | Class | `Admin` | `PolicyNames.AdminOnly` — the app-wide event settings write, incl. minting a new cycle, #1104/#1631 |

The Event tab's editable/read-only split is not a route gate: `EventSettingsTabViewComponent`
calls `IAuthorizationService.AuthorizeAsync(user, null, PolicyNames.AdminOnly)` and renders
the shared form only when it succeeds, else a read-only view with no `<form>` (#1628).

`/Settings` is reachable from the signed-in user menu (`SectionChrome` contributes it to the
`user-menu` chrome slot, unconditionally — same gate as the page's own `[Authorize]`).
