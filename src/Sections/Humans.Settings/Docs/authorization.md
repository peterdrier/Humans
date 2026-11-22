# Settings — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SettingsController` (`/Settings`) | Class | any authenticated member | `[Authorize]` — hosts the tabs sections contribute via `ISectionSettings` |
| `SettingsAdminController` POST (`/Settings/Admin`) | Class | `Admin` | `PolicyNames.AdminOnly` — the app-wide event settings write, incl. minting a new cycle; no GET |
| `SettingsAdminController` POST (`/Settings/Admin/Activate/{id}`) | Class | `Admin` | `PolicyNames.AdminOnly` — makes an event active and the previous one inactive |
| `SettingsAdminController` POST (`/Settings/Admin/TimeZone`) | Class | `Admin` | `PolicyNames.AdminOnly` — sets the global event timezone |

The Event tab's editable/read-only split is not a route gate: `EventSettingsTabViewComponent`
calls `IAuthorizationService.AuthorizeAsync(user, null, PolicyNames.AdminOnly)` and renders
the event list and the shared form only when it succeeds, else a read-only view of the active event with no
`<form>` and no list.

`/Settings` is reachable from the signed-in user menu (`ISectionChrome` contributes it to the
`user-menu` chrome slot, unconditionally — same gate as the page's own `[Authorize]`).
