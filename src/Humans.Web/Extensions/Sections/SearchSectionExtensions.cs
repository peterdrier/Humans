using Humans.Application.Interfaces.Search;
using Humans.Application.Services.Search;

namespace Humans.Web.Extensions.Sections;

internal static class SearchSectionExtensions
{
    /// <summary>
    /// Search is a UI-only orchestrator: it owns no tables, has no
    /// repository, and reaches every other section through public service
    /// interfaces. No caching decorator — at ~500-user scale the per-call
    /// fan-out is cheap and the result is page-specific (different
    /// query / filter / viewer permission), which is the wrong shape for
    /// caching anyway.
    /// </summary>
    internal static IServiceCollection AddSearchSection(this IServiceCollection services)
    {
        services.AddScoped<ISearchService, SearchService>();
        return services;
    }
}
