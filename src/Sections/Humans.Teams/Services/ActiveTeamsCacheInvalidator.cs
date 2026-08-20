using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>
/// Split out of <c>MemoryCacheInvalidators.cs</c> in G5 lane 3a-1
/// (nobodies-collective/Humans#866): its six siblings wrap <c>IMemoryCache</c> and moved to
/// Base, but this one injects <see cref="ITeamService"/> and so must live in the Teams
/// section.
/// </summary>
/// <remarks>
/// This is the one file that lane deliberately did NOT namespace-preserve. The rest of the
/// lane kept <c>Humans.Application.*</c> / <c>Humans.Domain.*</c> / <c>Humans.Infrastructure.*</c>
/// namespaces so 300+ call sites needed no edit; this class had exactly one consumer — the DI
/// registration in Web, which had to move here anyway once HUM0034 forced the type internal —
/// so keeping <c>Humans.Infrastructure.Caching</c> inside a section assembly would have bought
/// nothing and left a foreign namespace in Teams.
/// </remarks>
internal sealed class ActiveTeamsCacheInvalidator(ITeamService teamService) : IActiveTeamsCacheInvalidator
{
    public void Invalidate() => teamService.InvalidateActiveTeamsCache();
}
