using Humans.Base.Authorization;

namespace Humans.Teams.Contracts;

/// <summary>Teams as an owner kind for roles and permissions.</summary>
public static class TeamOwner
{
    public const string Kind = "Team";

    public static Owner Of(Guid teamId) => new(Kind, teamId);
}
