<!-- freshness:triggers
  src/Sections/Humans.Tickets/Controllers/**
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
-->

# Tickets — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TicketController` | Class | `TicketsAdmin, Admin, Board` | `PolicyNames.TicketAdminBoardOrAdmin` (`Orders`, `Attendees`, `Codes`, `WhoHasntBought`, `SalesAggregates` all inherit) |
| `TicketController.Sync` | Action | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.FullResync` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ParticipationBackfill` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ExportAttendees` | Action | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.ExportOrders` | Action | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.ExportAccountantReport` | Action | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.ExportDonations` | Action | `Admin` | `PolicyNames.AdminOnly` (donor-list CSV with buyer PII; pinned by `TicketControllerTests`) |
| `TicketTransferController` | Class | `[Authorize]` (authenticated) | — |
| `TicketParticipationController` (`/Tickets/DeclareNotAttending`, `/Tickets/UndoNotAttending`) | Class | `[Authorize]` (authenticated; acts on the signed-in member only, antiforgery POSTs) | — (exempt from `MembershipRequiredFilter`, as when Users served them) |
| `TicketTransferAdminController` | Class | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketsContactsAdminController` | Class | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketsOnsiteAdminController` | Class | `TicketsAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (gate staff check the onsite roster from the door alongside the scanner) |
| `TicketsGateAdminController` (`/Tickets/Admin/Gate`) | Class | `TicketsAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` (gate-terminal credential management; `Index` and `SetPassword` inherit the class policy) |

### `IAuthorizationService.AuthorizeAsync` note

`OnsiteChipViewComponent` (profile user part) renders for `ProfileCardViewMode.Self`, otherwise only when the viewer passes `PolicyNames.TicketAdminBoardOrAdmin`.

`TicketController.Index` gates finance-only metrics with `FinanceRoles.IsFinanceAdmin(User)` after the class-level policy.
