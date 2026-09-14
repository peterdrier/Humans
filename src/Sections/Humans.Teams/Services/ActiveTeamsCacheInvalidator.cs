using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>
/// Users' way of evicting the team graph after writes the service cannot see. Unlike the
/// <c>IMemoryCache</c>-backed invalidators in Base it injects <see cref="ITeamService"/>, so
/// the implementation lives here while the interface stays on the leaf.
/// </summary>
internal sealed class ActiveTeamsCacheInvalidator(ITeamService teamService) : IActiveTeamsCacheInvalidator
{
    public void Invalidate() => teamService.InvalidateActiveTeamsCache();
}
