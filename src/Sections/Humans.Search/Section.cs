using Humans.Base.Interfaces;
using Humans.Search.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Search;

public sealed class Section : SectionBase, ISectionNav
{
    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ISearchService, SearchService>();
    }

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
    [
        new(
            "Search",
            Controller: "Search",
            Action: "Index",
            Weight: 0,
            IconCssClass: "fa-solid fa-magnifying-glass",
            Visible: (_, user) => user.Identity?.IsAuthenticated == true)
    ];
}
