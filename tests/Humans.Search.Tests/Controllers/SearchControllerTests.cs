using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Search.Controllers;
using Humans.Search.Models;
using Humans.Search.Services;
using Humans.Search.Services.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Search.Tests.Controllers;

/// <summary>
/// <see cref="SearchController"/> owns two things the service deliberately does not:
/// display ordering (memory/architecture/display-sort-in-controllers.md) and view-model
/// assembly. Everything else on this page is a pass-through, so that is what is pinned here.
/// </summary>
public sealed class SearchControllerTests
{
    private readonly ISearchService _search = Substitute.For<ISearchService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ILogger<SearchController> _logger = Substitute.For<ILogger<SearchController>>();

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    public async Task Index_QueryShorterThanTwoChars_KeepsTheRawQuery_AndListsTheCategoriesForTheHint(string? query)
    {
        StubResults(new GlobalSearchResults((query ?? string.Empty).Trim(), null, [Bucket("Human")]));

        var vm = await IndexAsync(query, filter: null);

        vm.Query.Should().Be(query);
        vm.Buckets.Should().ContainSingle().Which.Label.Should().Be("Human label");
        vm.HasResults.Should().BeFalse();
    }

    [HumansFact]
    public async Task Index_TrimsTheQuery_ForwardsTheFilter_AndShowsTheServicesMatchedKey()
    {
        StubResults(new GlobalSearchResults("Kitchen", "Team", [Bucket("Team")]));

        var vm = await IndexAsync("  Kitchen  ", "team");

        await _search.Received(1).SearchAsync("Kitchen", "team", Arg.Any<CancellationToken>());
        vm.Filter.Should().Be("Team");
        vm.Query.Should().Be("Kitchen");
        vm.SearchFailed.Should().BeFalse();
    }

    [HumansFact]
    public async Task Index_SortsEveryBucket_ByScoreDescendingThenSortKeyAscending_KeepingCategoryOrder()
    {
        SearchHit[] hits = [Hit("beta", 60), Hit("Alpha", 60), Hit("Zulu", 100)];
        StubResults(new GlobalSearchResults("q", null, [Bucket("Human", hits), Bucket("Team", hits)]));

        var vm = await IndexAsync("query", filter: null);

        vm.Buckets.Select(b => b.Key).Should().Equal("Human", "Team");
        // SortKey tiebreak is case-insensitive: "Alpha" must beat "beta" despite the lowercase b.
        vm.Buckets.Should().AllSatisfy(b =>
            b.Hits.Select(h => h.SortKey).Should().Equal("Zulu", "Alpha", "beta"));
        vm.TotalCount.Should().Be(6);
    }

    [HumansFact]
    public async Task Index_PassesTheMatchContext_ThroughToTheView_Untouched()
    {
        var hit = new SearchHit(Guid.NewGuid(), "Sparkle", 60, "Bio", "…builds <em>kitchens</em>…");
        StubResults(new GlobalSearchResults("kitchen", null, [Bucket("Human", hit)]));

        var vm = await IndexAsync("kitchen", filter: null);

        vm.Buckets.Single().Hits.Should().ContainSingle().Which.Should().Be(hit);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Index_SearchThrows_RendersTheShellWithTheQueryPreserved_InsteadOfA500(bool dependencyCancelled)
    {
        Exception failure = dependencyCancelled
            ? new OperationCanceledException("section timed out")
            : new InvalidOperationException("section down");
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(failure);

        var vm = await IndexAsync("kitchen", "Camp");

        vm.Query.Should().Be("kitchen");
        vm.Filter.Should().Be("Camp");
        vm.SearchFailed.Should().BeTrue();
        vm.Buckets.Should().BeEmpty();
        var log = _logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).GetArguments();
        log[0].Should().Be(LogLevel.Error);
        log[3].Should().BeSameAs(failure);
    }

    [HumansFact]
    public async Task Index_RequestCancelled_PropagatesInsteadOfReturningA200Shell()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var act = async () => await BuildController().Index(
            "kitchen", null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var log = _logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).GetArguments();
        log[0].Should().Be(LogLevel.Warning);
        log[3].Should().BeNull();
    }

    private async Task<GlobalSearchViewModel> IndexAsync(string? query, string? filter)
    {
        var result = await BuildController().Index(query, filter, TestContext.Current.CancellationToken);

        return result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<GlobalSearchViewModel>().Subject;
    }

    private void StubResults(GlobalSearchResults results) =>
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(results));

    private SearchController BuildController()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var controller = new SearchController(_search, _users, _logger)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static SearchBucket Bucket(string key, params SearchHit[] hits) =>
        new(key, $"{key} label", "icon", typeof(object), hits);

    private static SearchHit Hit(string sortKey, int score) => new(Guid.NewGuid(), sortKey, score);
}
