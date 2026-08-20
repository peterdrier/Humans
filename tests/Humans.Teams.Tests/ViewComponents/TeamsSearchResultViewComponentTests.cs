using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Teams.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Teams.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="TeamsSearchResultViewComponent"/>: the global-search row for a team.
/// Callers pass a slug and nothing else (nobodies-collective/Humans#1062), so the fetch
/// and the empty-content fallbacks are the whole behaviour.
/// </summary>
public class TeamsSearchResultViewComponentTests
{
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();

    [HumansFact]
    public async Task Fetches_the_teams_own_display_fields_from_the_slug()
    {
        _teams.GetTeamBySlugAsync("kitchen", Arg.Any<CancellationToken>())
            .Returns(Team("Kitchen", "kitchen"));

        var result = await new TeamsSearchResultViewComponent(_teams).InvokeAsync("kitchen");

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Subject.ViewData!.Model.Should().BeOfType<TeamsSearchResultViewModel>().Subject;
        model.Name.Should().Be("Kitchen");
        model.Slug.Should().Be("kitchen");
    }

    [HumansFact]
    public async Task Renders_nothing_for_an_unknown_or_missing_slug()
    {
        _teams.GetTeamBySlugAsync("gone", Arg.Any<CancellationToken>()).Returns((TeamInfo?)null);
        var sut = new TeamsSearchResultViewComponent(_teams);

        (await sut.InvokeAsync("gone"))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        (await sut.InvokeAsync("  "))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _teams.DidNotReceive().GetTeamBySlugAsync("  ", Arg.Any<CancellationToken>());
    }

    private static TeamInfo Team(string name, string slug) => new(
        Guid.NewGuid(), name, null, slug,
        IsActive: true, IsSystemTeam: false, SystemTeamType: Base.Enums.SystemTeamType.None,
        RequiresApproval: false, IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
        CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0), Members: []);
}
