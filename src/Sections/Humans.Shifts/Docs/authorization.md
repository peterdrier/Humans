# Shifts — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ShiftsController` | Class | `[Authorize]` (authenticated) | — |
| `ShiftsController.ToggleDay` | Action | `[Authorize]` inherited (`[HttpPost("ToggleDay")]`, `[ValidateAntiForgeryToken]`) | — (self-service day-rota toggle; name/dietary gates short-circuit) |
| `ShiftsController.Summary` / `SummaryTeam` / `SummaryRota` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` OR any team manager/coordinator | `PolicyNames.ShiftDepartmentManager` (read-only Shift Summary by Camp at `/Shifts/Summary`) |
| `ShiftsController.Settings` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftsController.OrphanSignups` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `ShiftAdminController.MoveRota` | Action | `Admin, VolunteerCoordinator` | `PolicyNames.VolunteerManager` |
| `ShiftDashboardController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` OR any team manager/coordinator (custom handler) | `PolicyNames.ShiftDepartmentManager` |
| `ShiftDashboardController.PostEventStats` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftDashboardController.SearchVolunteers` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftDashboardController.Voluntell` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftWorkloadAdminController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `VolunteerTrackingController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `VolunteerTrackingController.SetCampSetup` / `ClearCampSetup` / `SetDayOff` / `ClearDayOff` / `SetAvailabilityDay` / `ClearAvailabilityDay` | Action | `Admin, VolunteerCoordinator` | `PolicyNames.VolunteerTrackingWrite` |
| `ShiftProfileController` (`[Route("Profile")]`) | Class | `[Authorize]` (authenticated) | — (`Me/ShiftInfo` GET/POST, the self-service shift-info panel embedded on the profile page) |

`EarlyEntryRosterController` lives in `Humans.EarlyEntry` — see that section's authorization doc.

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `IsAnyTeamManagerOrCoordinatorHandler` | `IsAnyTeamManagerOrCoordinatorRequirement` | none (role + coordinator-team-ids claims check) | `Authorization/IsAnyTeamManagerOrCoordinatorHandler.cs` (registered in the section's own `Section.cs` DI; backs `PolicyNames.ShiftDepartmentManager`) |
