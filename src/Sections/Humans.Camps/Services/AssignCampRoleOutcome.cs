namespace Humans.Camps.Services;

/// <summary>
/// Result of <see cref="ICampRoleService.AssignAsync"/>. Mapped by controllers
/// to TempData messages. <see cref="Assigned"/> is the only successful outcome.
/// </summary>
internal enum AssignCampRoleOutcome
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
