# Shifts — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ShiftsController` | Class | `[Authorize]` (authenticated) | — |
| `ShiftsController.ToggleDay` | Action | `[Authorize]` inherited (`[HttpPost("ToggleDay")]`, `[ValidateAntiForgeryToken]`) | — (self-service day-rota toggle; the name gate short-circuits) |
| `ShiftsController.Summary` / `SummaryTeam` / `SummaryRota` | Action | `Admin, Board, ShiftsNoInfo, ShiftsAdmin` OR any team manager/coordinator | `ShiftsPolicies.DepartmentRead` (read-only Shift Summary by Camp at `/Shifts/Summary`) |
| `ShiftsController.Settings` (POST; the read side is the `/Settings#shifts` tab) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftsController.OrphanSignups` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `ShiftAdminController.Index` | In-method | Department managers and approvers, or `ShiftsPolicies.Read` (Board) read-only | `ShiftsPolicies.Read` as a read-only fallback |
| `ShiftAdminController.MoveRota` | Action | `Admin, ShiftsAdmin` | `ShiftsPolicies.ManageVolunteers` |
| `ShiftDashboardController` | Class | `Admin, Board, ShiftsNoInfo, ShiftsAdmin` OR any team manager/coordinator (custom handler) | `ShiftsPolicies.DepartmentRead` |
| `ShiftDashboardController.PostEventStats` | Action | `Admin, Board, ShiftsNoInfo, ShiftsAdmin` | `ShiftsPolicies.Read` |
| `ShiftDashboardController.SearchVolunteers` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` | `ShiftsPolicies.Manage` |
| `ShiftDashboardController.Voluntell` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` | `ShiftsPolicies.Manage` |
| `ShiftWorkloadAdminController` | Class | `Admin, Board, ShiftsNoInfo, ShiftsAdmin` | `ShiftsPolicies.Read` |
| `VolunteerTrackingController` | Class | `Admin, Board, ShiftsNoInfo, ShiftsAdmin` | `ShiftsPolicies.Read`; `ExportXlsx` carries `ShiftsPolicies.Manage` (`Admin, ShiftsNoInfo, ShiftsAdmin`) |
| `VolunteerTrackingController.SetCampSetup` / `ClearCampSetup` / `SetDayOff` / `ClearDayOff` / `SetAvailabilityDay` / `ClearAvailabilityDay` | Action | `Admin, ShiftsAdmin` | `ShiftsPolicies.ManageVolunteers` |
| `ShiftProfileController` (`[Route("Shifts")]`) | Class | `[Authorize]` (authenticated) | — (`ShiftInfo` GET/POST, the self-service shift-info panel embedded on the profile page) |

`EarlyEntryRosterController` lives in `Humans.EarlyEntry` — see that section's authorization doc.

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `IsAnyTeamManagerOrCoordinatorHandler` | `IsAnyTeamManagerOrCoordinatorRequirement` | none (role + coordinator-team-ids claims check) | `Authorization/IsAnyTeamManagerOrCoordinatorHandler.cs` (registered in the section's own `Section.cs` DI; backs `ShiftsPolicies.DepartmentRead`) |
