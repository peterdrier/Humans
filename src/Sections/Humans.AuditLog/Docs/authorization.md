# AuditLog — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AuditLogController` | Class | `[Route("AuditLog")]` only — no class-level `[Authorize]` | — |
| `AuditLogController.Index` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (the only action — `CheckDriveActivity`/`Resource`/`Human` live on `MonitorController` in `Humans.Monitor`) |
