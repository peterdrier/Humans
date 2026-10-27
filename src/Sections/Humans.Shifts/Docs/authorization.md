# Shifts — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ShiftsController` | Class | `[Authorize]` (authenticated) | — |
| `ShiftsController.ToggleDay` | Action | `[Authorize]` inherited (`[HttpPost("ToggleDay")]`, `[ValidateAntiForgeryToken]`) | — (self-service day-rota toggle; the name gate short-circuits) |
| `ShiftsController.Summary` / `SummaryTeam` / `SummaryRota` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` OR any team manager/coordinator | `PolicyNames.ShiftDepartmentManager` (read-only Shift Summary by Camp at `/Shifts/Summary`) |
| `ShiftsController.Settings` (POST; the read side is the `/Settings#shifts` tab) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftsController.OrphanSignups` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `ShiftAdminController.MoveRota` | Action | `Admin, ShiftsAdmin` | `PolicyNames.VolunteerManager` |
| `ShiftDashboardController` | Class | `Admin, ShiftsNoInfo, ShiftsAdmin` OR any team manager/coordinator (custom handler) | `PolicyNames.ShiftDepartmentManager` |
| `ShiftDashboardController.PostEventStats` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftDashboardController.SearchVolunteers` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftDashboardController.Voluntell` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftWorkloadAdminController` | Class | `Admin, ShiftsNoInfo, ShiftsAdmin` | `PolicyNames.ShiftDashboardAccess` |
| `VolunteerTrackingController` | Class | `Admin, ShiftsNoInfo, ShiftsAdmin` | `PolicyNames.ShiftDashboardAccess` |
| `VolunteerTrackingController.SetCampSetup` / `ClearCampSetup` / `SetDayOff` / `ClearDayOff` / `SetAvailabilityDay` / `ClearAvailabilityDay` | Action | `Admin, ShiftsAdmin` | `PolicyNames.VolunteerTrackingWrite` |
| `ShiftProfileController` (`[Route("Shifts")]`) | Class | `[Authorize]` (authenticated) | — (`ShiftInfo` GET/POST, the self-service shift-info panel embedded on the profile page) |

`EarlyEntryRosterController` lives in `Humans.EarlyEntry` — see that section's authorization doc.

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `IsAnyTeamManagerOrCoordinatorHandler` | `IsAnyTeamManagerOrCoordinatorRequirement` | none (role + coordinator-team-ids claims check) | `Authorization/IsAnyTeamManagerOrCoordinatorHandler.cs` (registered in the section's own `Section.cs` DI; backs `PolicyNames.ShiftDepartmentManager`) |
