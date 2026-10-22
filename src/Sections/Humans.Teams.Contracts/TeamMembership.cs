namespace Humans.Teams.Contracts;

/// <summary>Built by consumers from <see cref="ITeamService.GetUserTeamMembershipsAsync"/>; nothing in Teams produces it.</summary>
public sealed record TeamMembership(string TeamName, TeamMemberRole Role)
{
    public bool IsHidden { get; init; }
}
