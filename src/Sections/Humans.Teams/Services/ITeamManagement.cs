using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>One injection for the section's own code; every face resolves to the same <c>CachingTeamService</c>.</summary>
internal interface ITeamManagement : ITeamService, ITeamWrite
{
}
