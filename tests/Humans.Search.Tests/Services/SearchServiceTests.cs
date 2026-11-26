using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Search.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Search.Tests.Services;

/// <summary>
/// The orchestration only: the length gate, trimming, the filter, enablement and category metadata. What each category searches
/// (field masks, visibility, the unbounded cap) is pinned beside its contributor in the owning section's tests.
/// </summary>
public sealed class SearchServiceTests
{
    private readonly FakeCategory _humans = new("Human", 0);
    private readonly FakeCategory _teams = new("Team", 10);
    private readonly FakeCategory _events = new("Event", 40);

    [HumansTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("  k  ")]
    public async Task SearchAsync_QueryShorterThanTwoCharsAfterTrim_ListsEveryCategoryEmpty_AndSearchesNone(string query)
    {
        var results = await Build().SearchAsync(query, ct: TestContext.Current.CancellationToken);

        results.Query.Should().Be(query.Trim());
        results.Buckets.Select(b => b.Key).Should().BeEquivalentTo(["Human", "Team", "Event"]);
        results.Buckets.Should().AllSatisfy(b => b.Hits.Should().BeEmpty());
        AllCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchAsync_TwoCharQuery_IsAtTheGateBoundary_AndFansOut()
    {
        await Build().SearchAsync("ki", ct: TestContext.Current.CancellationToken);

        AllCalls().Should().Equal("Human:ki", "Team:ki", "Event:ki");
    }

    [HumansFact]
    public async Task SearchAsync_TrimsQuery_BeforeFanOut_AndEchoesTheTrimmedForm()
    {
        var results = await Build().SearchAsync("  Kitchen  ", ct: TestContext.Current.CancellationToken);

        results.Query.Should().Be("Kitchen");
        AllCalls().Should().Equal("Human:Kitchen", "Team:Kitchen", "Event:Kitchen");
    }

    [HumansTheory]
    [InlineData("Team")]
    [InlineData("team")]
    public async Task SearchAsync_Filter_SearchesOnlyThatCategory_AndStillListsTheOthers(string filter)
    {
        var results = await Build().SearchAsync("Kitchen", filter, TestContext.Current.CancellationToken);

        results.Filter.Should().Be("Team");
        AllCalls().Should().Equal("Team:Kitchen");
        results.Buckets.Single(b => string.Equals(b.Key, "Team", StringComparison.Ordinal)).Hits.Should().ContainSingle();
        results.Buckets.Where(b => !string.Equals(b.Key, "Team", StringComparison.Ordinal)).Should().AllSatisfy(b => b.Hits.Should().BeEmpty());
    }

    [HumansFact]
    public async Task SearchAsync_UnknownFilter_SearchesEveryCategory()
    {
        var results = await Build().SearchAsync("Kitchen", "Nope", TestContext.Current.CancellationToken);

        results.Filter.Should().BeNull();
        AllCalls().Should().HaveCount(3);
    }

    [HumansFact]
    public async Task SearchAsync_DisabledCategory_IsNeitherListedNorSearched_EvenWhenFiltered()
    {
        _events.Enabled = false;

        var results = await Build().SearchAsync("Kitchen", "Event", TestContext.Current.CancellationToken);

        results.Filter.Should().BeNull();
        results.Buckets.Select(b => b.Key).Should().BeEquivalentTo(["Human", "Team"]);
        _events.Calls.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchAsync_CarriesCategoryWeightsWithoutChoosingDisplayOrder()
    {
        var results = await new SearchService([new FakeContributor(_events, _teams, _humans)], Services())
            .SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        results.Buckets.Select(b => (b.Key, b.Weight)).Should().BeEquivalentTo([("Human", 0), ("Team", 10), ("Event", 40)]);
    }

    [HumansFact]
    public async Task SearchAsync_PassesEachContributorsHitsAndLabelThrough_WithoutRescoring()
    {
        var hit = new SearchHit(Guid.NewGuid(), "Nothing Like The Query", 40, "Bio", "…kitchen…");
        _teams.Hits = [hit];

        var results = await Build().SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        var team = results.Buckets.Single(b => string.Equals(b.Key, "Team", StringComparison.Ordinal));
        team.Label.Should().Be("Team label");
        team.IconCssClass.Should().Be("icon-Team");
        team.ResultComponent.Should().Be<FakeCategory>();
        team.Hits.Should().ContainSingle().Which.Should().Be(hit);
    }

    private SearchService Build() => new([new FakeContributor(_humans, _teams, _events)], Services());

    private static ServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private IEnumerable<string> AllCalls() => _humans.Calls.Concat(_teams.Calls).Concat(_events.Calls);

    private sealed class FakeCategory(string key, int weight)
    {
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<SearchHit> Hits { get; set; } = [new SearchHit(Guid.NewGuid(), key, 100)];
        public List<string> Calls { get; } = [];

        public SearchCategory ToCategory() => new(
            key,
            _ => $"{key} label",
            $"icon-{key}",
            typeof(FakeCategory),
            (_, query, _) =>
            {
                Calls.Add($"{key}:{query}");
                return Task.FromResult(Hits);
            },
            weight,
            _ => Enabled);
    }

    private sealed class FakeContributor(params FakeCategory[] categories) : ISearchContributor
    {
        public IEnumerable<SearchCategory> Categories() => categories.Select(c => c.ToCategory());
    }
}
