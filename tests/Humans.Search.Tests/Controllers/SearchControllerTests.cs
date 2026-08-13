using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Users;
using Humans.Search.Controllers;
using Humans.Search.Models;
using Humans.Search.Services;
using Humans.Search.Services.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    public async Task Index_QueryShorterThanTwoChars_RendersTheShell_WithoutSearching(string? query)
    {
        var vm = await IndexAsync(query, filter: null);

        vm.Query.Should().Be(query);
        vm.HumanResults.Should().BeEmpty();
        vm.TeamResults.Should().BeEmpty();
        vm.CampResults.Should().BeEmpty();
        vm.ShiftResults.Should().BeEmpty();
        vm.EventResults.Should().BeEmpty();
        await _search.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<SearchResultType?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_TrimsTheQuery_AndForwardsTheFilterToTheService()
    {
        StubResults(new GlobalSearchResults("Kitchen", [], [], [], [], []));

        var vm = await IndexAsync("  Kitchen  ", SearchResultType.Team);

        await _search.Received(1).SearchAsync("Kitchen", SearchResultType.Team, Arg.Any<CancellationToken>());
        vm.Filter.Should().Be(SearchResultType.Team);
        vm.Query.Should().Be("Kitchen");
    }

    [HumansFact]
    public async Task Index_SortsEveryNonHumanBucket_ByScoreDescendingThenTitleAscending()
    {
        var teams = new[]
        {
            Hit(SearchResultType.Team, "beta", 60),
            Hit(SearchResultType.Team, "Alpha", 60),
            Hit(SearchResultType.Team, "Zulu", 100),
        };
        StubResults(new GlobalSearchResults(
            "q", [], teams, Retype(teams, SearchResultType.Camp),
            Retype(teams, SearchResultType.Shift), Retype(teams, SearchResultType.Event)));

        var vm = await IndexAsync("query", filter: null);

        // Title tiebreak is case-insensitive: "Alpha" must beat "beta" despite the lowercase b.
        vm.TeamResults.Select(r => r.Title).Should().Equal("Zulu", "Alpha", "beta");
        vm.CampResults.Select(r => r.Title).Should().Equal("Zulu", "Alpha", "beta");
        vm.ShiftResults.Select(r => r.Title).Should().Equal("Zulu", "Alpha", "beta");
        vm.EventResults.Select(r => r.Title).Should().Equal("Zulu", "Alpha", "beta");
    }

    [HumansFact]
    public async Task Index_SortsHumans_ByRelevanceThenBurnerName()
    {
        StubResults(new GlobalSearchResults(
            "ian",
            [
                Human("Adrian", 60),
                Human("Brian", 60),
                Human("Ian", 100),
            ],
            [], [], [], []));

        var vm = await IndexAsync("ian", filter: null);

        vm.HumanResults.Select(r => r.BurnerName).Should().Equal("Ian", "Adrian", "Brian");
    }

    [HumansFact]
    public async Task Index_ProjectsEveryHumanField_OntoTheSharedPartialsViewModel()
    {
        var hit = new HumanSearchResult(
            UserId: Guid.NewGuid(),
            ProfileId: Guid.NewGuid(),
            BurnerName: "Sparkle",
            ProfilePictureUrl: "/uploads/profiles/sparkle.jpg",
            MatchField: "Bio",
            MatchSnippet: "…builds <em>kitchens</em>…",
            MatchedEmail: null,
            Score: 60);
        StubResults(new GlobalSearchResults("kitchen", [hit], [], [], [], []));

        var vm = await IndexAsync("kitchen", filter: null);

        var row = vm.HumanResults.Should().ContainSingle().Subject;
        row.UserId.Should().Be(hit.UserId);
        row.BurnerName.Should().Be(hit.BurnerName);
        row.ProfilePictureUrl.Should().Be(hit.ProfilePictureUrl);
        row.MatchField.Should().Be(hit.MatchField);
        row.MatchSnippet.Should().Be(hit.MatchSnippet);
        row.MatchedEmail.Should().BeNull();
    }

    [HumansFact]
    public async Task Index_SearchThrows_RendersTheShellWithTheQueryPreserved_InsteadOfA500()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchResultType?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("section down"));

        var vm = await IndexAsync("kitchen", SearchResultType.Camp);

        vm.Query.Should().Be("kitchen");
        vm.Filter.Should().Be(SearchResultType.Camp);
        vm.TeamResults.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Index_RequestCancelled_PropagatesInsteadOfReturningA200Shell()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchResultType?>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var act = async () => await BuildController().Index(
            "kitchen", null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<GlobalSearchViewModel> IndexAsync(string? query, SearchResultType? filter)
    {
        var result = await BuildController().Index(query, filter, TestContext.Current.CancellationToken);

        return result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<GlobalSearchViewModel>().Subject;
    }

    private void StubResults(GlobalSearchResults results) =>
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchResultType?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(results));

    private SearchController BuildController()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var controller = new SearchController(_search, _users, NullLogger<SearchController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static GlobalSearchResult Hit(SearchResultType type, string title, int score) =>
        new(type, title, $"{title}-subtitle", $"/{type}/{title}", score);

    private static IReadOnlyList<GlobalSearchResult> Retype(
        IReadOnlyList<GlobalSearchResult> hits, SearchResultType type) =>
        hits.Select(h => h with { Type = type }).ToList();

    private static HumanSearchResult Human(string burnerName, int score) =>
        new(Guid.NewGuid(), Guid.NewGuid(), burnerName, null, "Name", null, null, score);
}
