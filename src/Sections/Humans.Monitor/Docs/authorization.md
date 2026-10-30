<!-- freshness:triggers
  src/Sections/Humans.Monitor/Controllers/MonitorController.cs
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
  src/Humans.Base/Authorization/PolicyNames.cs
-->

# Monitor — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `MonitorController` | Class | `[Route("Monitor")]` only — no class-level `[Authorize]` | — |
| `MonitorController.CheckDriveActivity` | Action | `Admin` | `PolicyNames.AdminOnly` |
