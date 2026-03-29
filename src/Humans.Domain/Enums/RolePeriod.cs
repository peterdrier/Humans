namespace Humans.Domain.Enums;

/// <summary>
/// Period tag on a TeamRoleDefinition indicating when the role is active.
/// Stored as string in DB. Used for roster page filtering.
/// </summary>
public enum RolePeriod
{
    YearRound = 0,
    Build = 1,
    Event = 2,
    Strike = 3
}
