using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Events.Contracts;
using Humans.Events.ViewComponents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Events.Tests.Contributions;

public sealed class SearchContributorTests
{
    private readonly IEventService _events = Substitute.For<IEventService>();

    [HumansFact]
    public async Task Category_SearchesWithAnUnboundedCap_AndPassesKeySortKeyAndScoreThrough()
    {
        var id = Guid.NewGuid();
        _events.SearchAsync("Kitchen", int.MaxValue, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EventSearchHit>>([new EventSearchHit(id, "Kitchen Takeover", 40)]));
        var services = Services(eventsEnabled: true);

        var category = Category();
        var hits = await category.SearchAsync(services, "Kitchen", TestContext.Current.CancellationToken);

        category.Label(services).Should().Be("Events");
        category.ResultComponent.Should().Be<EventsSearchResultViewComponent>();
        hits.Should().ContainSingle().Which.Should().Be(new SearchHit(id, "Kitchen Takeover", 40));
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Category_IsEnabledOnlyWithTheEventsFeatureFlag(bool eventsEnabled) =>
        Category().IsEnabled!(Services(eventsEnabled)).Should().Be(eventsEnabled);

    private static SearchCategory Category() =>
        ((ISearchContributor)new Section()).Categories().Should().ContainSingle().Subject;

    private ServiceProvider Services(bool eventsEnabled)
    {
        var localizer = Substitute.For<IStringLocalizer<EventsResource>>();
        localizer["Events_Search_Category"].Returns(new LocalizedString("Events_Search_Category", "Events"));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Features:Events"] = eventsEnabled ? "true" : "false",
            })
            .Build();
        return new ServiceCollection().AddSingleton(_events).AddSingleton(localizer).AddSingleton(configuration)
            .BuildServiceProvider();
    }
}
