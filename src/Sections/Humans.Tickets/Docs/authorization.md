# Tickets — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TicketController` | Class | `TicketAdmin, Admin, Board` | `PolicyNames.TicketAdminBoardOrAdmin` (`Orders`, `Attendees`, `Codes`, `GateList`, `WhoHasntBought`, `SalesAggregates` all inherit) |
| `TicketController.Sync` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.FullResync` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ParticipationBackfill` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ExportAttendees` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.ExportOrders` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketTransferController` | Class | `[Authorize]` (authenticated) | — |
| `TicketTransferAdminController` | Class | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketsContactsAdminController` | Class | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketsOnsiteAdminController` | Class | `TicketAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (gate staff check the onsite roster from the door alongside the scanner) |
| `TicketsGateAdminController` (`/Tickets/Admin/Gate`) | Class | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` (gate-terminal credential management; `Index` and `SetPassword` inherit the class policy) |

### `IAuthorizationService.AuthorizeAsync` note

`TicketController.Index` gates finance-only metrics with `RoleChecks.CanAccessFinance(User)` after the class-level policy.
