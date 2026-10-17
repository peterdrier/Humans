using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>
/// The Teams section's internal face: the public <see cref="ITeamService"/> reads and
/// <see cref="ITeamWrite"/> writes under one name, so the section's own code injects a single
/// interface. Every face resolves to the same <c>CachingTeamService</c> singleton.
/// </summary>
internal interface ITeamManagement : ITeamService, ITeamWrite
{
}
