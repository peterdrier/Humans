using Humans.Base.Interfaces;
using Humans.Search.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Search;

/// <summary>
/// Search's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// No <c>AddSectionDbContext</c> line: the section owns no tables. It is a pure read
/// orchestrator over five other sections' service interfaces, so there is no repository, no
/// <c>PeeledConfigurationNamespaces</c> row and no migration.
/// <para>
/// No caching decorator either, and that is a decision rather than an omission: four of the
/// five buckets are already served from their owning section's warm in-memory snapshot
/// (<c>CachingUserService</c>, <c>CachingTeamService</c>, <c>CachingCampService</c>,
/// <c>CachingEventService</c>), so a Search-level cache would cache a cache and duplicate
/// invalidation those sections already do correctly. <c>Docs/Search.md</c> records it.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ISearchService, SearchService>();
    }
}
