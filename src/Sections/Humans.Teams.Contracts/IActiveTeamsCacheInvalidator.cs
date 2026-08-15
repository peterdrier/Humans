using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for the <c>CacheKeys.ActiveTeams</c> in-memory
/// team directory cache owned by the Teams section. Background jobs and other
/// services that change team membership outside of <c>ITeamService</c>'s own
/// write paths inject this and call <see cref="Invalidate"/> after their own
/// writes — they never touch <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
/// directly. Added during the Google-writing Hangfire-jobs migration (issue
/// #570) so <c>SystemTeamSyncJob</c> can drop its direct cache dependency.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing cross-section flush of the Teams active-team directory cache from background jobs (#570); remains until those writes go through ITeamService.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IActiveTeamsCacheInvalidator : IInvalidator
{
    /// <summary>
    /// Evicts the active-teams master cache entry so the next read repopulates
    /// from the database via <c>ITeamService</c>.
    /// </summary>
    void Invalidate();
}
