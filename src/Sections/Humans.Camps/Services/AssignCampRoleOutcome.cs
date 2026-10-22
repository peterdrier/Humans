namespace Humans.Camps.Services;

/// <summary><see cref="Assigned"/> is the only successful outcome.</summary>
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
