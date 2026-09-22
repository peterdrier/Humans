# Email — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `EmailController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `EmailPreviewController` | Class | any authenticated human | `[Authorize]` |
