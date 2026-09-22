# AuditLog — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AuditLogController` | Class | `[Route("AuditLog")]` only — no class-level `[Authorize]` | — |
| `AuditLogController.Index` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (the only action — `CheckDriveActivity` lives on `MonitorController` in `Humans.Monitor`; `Resource`/`Human` on `GoogleController` in `Humans.GoogleIntegration`) |
