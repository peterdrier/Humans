using Humans.Testing;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;
using Humans.Teams.ViewComponents;

namespace Humans.Teams.Tests.Contributions;

public sealed class SearchContributorTests
{
    [HumansFact]
    public async Task Category_SearchesWithAnUnboundedCap_AndPassesKeySortKeyAndScoreThrough()
    {
        var id = Guid.NewGuid();
        var service = Substitute.For<ITeamService>();
        service.SearchAsync("Kitchen", int.MaxValue, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TeamSearchHit>>([new TeamSearchHit(id, "Kitchen", 80)]));
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>().AddFormatting();
        localizer["Teams_Search_Category"].Returns(new LocalizedString("Teams_Search_Category", "Teams"));
        var services = new ServiceCollection().AddSingleton(service).AddSingleton(localizer).BuildServiceProvider();

        var category = ((ISearchContributor)new Section()).Categories().Should().ContainSingle().Subject;
        var hits = await category.SearchAsync(services, "Kitchen", TestContext.Current.CancellationToken);

        category.Label(services).Should().Be("Teams");
        category.ResultComponent.Should().Be<TeamsSearchResultViewComponent>();
        hits.Should().ContainSingle().Which.Should().Be(new SearchHit(id, "Kitchen", 80));
    }
}
