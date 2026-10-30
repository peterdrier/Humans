<!-- freshness:triggers
  src/Sections/Humans.Tickets/Controllers/**
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
-->

# Tickets — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TicketController` | Class | `TicketsAdmin, Admin, Board` | `TicketsPolicies.Read` (`Orders`, `Attendees`, `Codes`, `WhoHasntBought`, `SalesAggregates` all inherit) |
| `TicketController.Sync` | Action | `TicketsAdmin, Admin` | `TicketsPolicies.Manage` |
| `TicketController.FullResync` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ParticipationBackfill` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ExportAttendees` | Action | `TicketsAdmin, Admin` | `TicketsPolicies.Manage` |
| `TicketController.ExportOrders` | Action | `TicketsAdmin, Admin` | `TicketsPolicies.Manage` |
| `TicketController.ExportAccountantReport` | Action | `TicketsAdmin, Admin` | `TicketsPolicies.Manage` |
| `TicketController.ExportDonations` | Action | `Admin` | `PolicyNames.AdminOnly` (donor-list CSV with buyer PII; pinned by `TicketControllerTests`) |
| `TicketTransferController` | Class | `[Authorize]` (authenticated) | — |
| `TicketParticipationController` (`/Tickets/DeclareNotAttending`, `/Tickets/UndoNotAttending`) | Class | `[Authorize]` (authenticated; acts on the signed-in member only, antiforgery POSTs) | — (both actions carry `[AllowBeforeActiveMembership]`, as when Users served them) |
| `TicketTransferAdminController` | Class | `TicketsAdmin, Board, Admin` | `TicketsPolicies.Read`; `Decide` carries `TicketsPolicies.Manage` |
| `TicketsContactsAdminController` | Class | `TicketsAdmin, Board, Admin` | `TicketsPolicies.Read`; `Apply` carries `TicketsPolicies.Manage` |
| `TicketsOnsiteAdminController` | Class | `TicketsAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (gate staff check the onsite roster from the door alongside the scanner) |
| `TicketsGateAdminController` (`/Tickets/Admin/Gate`) | Class | `TicketsAdmin, Board, Admin` | `TicketsPolicies.Read` (gate-terminal credential status); `SetPassword` carries `TicketsPolicies.Manage` |

### `IAuthorizationService.AuthorizeAsync` note

`OnsiteChipViewComponent` (profile user part) renders for `ProfileCardViewMode.Self`, otherwise only when the viewer passes `TicketsPolicies.Read`.

`TicketController.Index` gates finance-only metrics with `FinanceRoles.IsFinanceAdmin(User)` after the class-level policy.
