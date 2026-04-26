namespace Humans.Application.Services.Camps;

/// <summary>
/// Result of <see cref="ICampRoleService.AssignAsync"/>. Mapped by controllers
/// to TempData messages. <see cref="Assigned"/> is the only successful outcome.
/// </summary>
public enum AssignCampRoleOutcome
{
    Assigned,
    SeasonNotFound,
    RoleNotFound,
    RoleDeactivated,
    MemberNotFound,
    MemberNotActive,
    MemberSeasonMismatch,
    SlotCapReached,
    AlreadyHoldsRole
}
