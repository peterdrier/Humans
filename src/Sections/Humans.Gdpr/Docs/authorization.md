# Gdpr — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GuestDataController` | Class | `[Authorize]` (authenticated) | — (Article 15 data export for profileless accounts; exports the caller's own data only, never an id from the request) |
