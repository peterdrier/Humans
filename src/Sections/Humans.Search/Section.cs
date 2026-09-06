using Humans.Base.Interfaces;
using Humans.Search.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Search;

/// <summary>
/// Search's DI entry point. No <c>DbContext</c> and no caching decorator: the section owns
/// no tables, and four of the five buckets already come from their owners' caches
/// (<c>Docs/Search.md</c>).
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ISearchService, SearchService>();
    }
}
