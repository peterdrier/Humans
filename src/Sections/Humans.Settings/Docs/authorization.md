# Settings — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SettingsAdminController` (`/Settings/Admin`) | Class | `Admin` | `PolicyNames.AdminOnly` — the app-wide event settings screen, #1104 |
| `EventSettingsCarryAdminController` (`/Settings/Admin/Carry`) | Class | `Admin` | `PolicyNames.AdminOnly` — copies event values off the Shifts-owned rows into `settings_event`, #1104; retires once the Shifts columns are dropped |
