# Notifications — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `NotificationsController` | Class | `[Authorize]` (authenticated) | — |
| `NotificationApiController` | Class | Configured `X-Api-Key` mapped to one configured user | `NotificationApiKeyAuthFilter` |
