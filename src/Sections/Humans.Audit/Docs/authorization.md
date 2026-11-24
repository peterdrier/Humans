# Audit — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AuditController` | Class | `[Route("Audit")]` only — no class-level `[Authorize]` | — |
| `AuditController.Index` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (the only action — `CheckDriveActivity` lives on `MonitorController` in `Humans.Monitor`; `Resource`/`Human` on `GoogleController` in `Humans.Google`) |
