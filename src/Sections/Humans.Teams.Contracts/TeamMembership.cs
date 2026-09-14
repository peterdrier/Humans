namespace Humans.Teams.Contracts;

/// <summary>
/// A user's active membership on a single team — just the team name and the user's
/// role. Users' profile popover and Agent's user snapshot build it themselves from
/// <see cref="ITeamServiceRead.GetUserTeamMembershipsAsync"/>; nothing in Teams
/// produces it.
/// </summary>
public sealed record TeamMembership(string TeamName, TeamMemberRole Role)
{
    public bool IsHidden { get; init; }
}
