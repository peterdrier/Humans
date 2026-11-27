# AuditLog — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AuditLogController` | Class | `[Route("AuditLog")]` only — no class-level `[Authorize]` | — |
| `AuditLogController.Index` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (`CheckDriveActivity` lives on `MonitorController` in `Humans.Monitor`; `Resource`/`Human` on `GoogleController` in `Humans.Google`) |
| `AuditLogController.Actions` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` — every declared action and its kept side |
