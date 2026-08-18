# Monitor — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `MonitorController` | Class | `[Route("Monitor")]` only — no class-level `[Authorize]` | — |
| `MonitorController.CheckDriveActivity` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `MonitorController.Resource` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `MonitorController.Human` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
