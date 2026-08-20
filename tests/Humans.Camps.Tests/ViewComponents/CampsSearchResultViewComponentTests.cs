using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Camps.Contracts;
using Humans.Camps.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Camps.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="CampsSearchResultViewComponent"/>: the global-search row for a camp.
/// Callers pass a slug and nothing else (nobodies-collective/Humans#1062), so resolving the
/// public-year season name — the label the orchestrator used to carry — is the behaviour.
/// </summary>
public class CampsSearchResultViewComponentTests
{
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();

    [HumansFact]
    public async Task Renders_the_public_year_season_name()
    {
        StubSettings(publicYear: 2026);
        _camps.GetCampBySlugAsync("garden", Arg.Any<CancellationToken>())
            .Returns(Camp("garden", (2025, "Old Name"), (2026, "Garden of Joy")));

        var result = await new CampsSearchResultViewComponent(_camps).InvokeAsync("garden");

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Subject.ViewData!.Model.Should().BeOfType<CampsSearchResultViewModel>().Subject;
        model.Name.Should().Be("Garden of Joy");
        model.Slug.Should().Be("garden");
    }

    [HumansFact]
    public async Task Falls_back_to_the_slug_when_the_camp_has_no_season_that_year()
    {
        StubSettings(publicYear: 2026);
        _camps.GetCampBySlugAsync("garden", Arg.Any<CancellationToken>())
            .Returns(Camp("garden", (2024, "Long Ago")));

        var result = await new CampsSearchResultViewComponent(_camps).InvokeAsync("garden");

        result.Should().BeOfType<ViewViewComponentResult>()
            .Subject.ViewData!.Model.Should().BeOfType<CampsSearchResultViewModel>()
            .Subject.Name.Should().Be("garden");
    }

    [HumansFact]
    public async Task Renders_nothing_for_an_unknown_or_missing_slug()
    {
        _camps.GetCampBySlugAsync("gone", Arg.Any<CancellationToken>()).Returns((CampInfo?)null);
        var sut = new CampsSearchResultViewComponent(_camps);

        (await sut.InvokeAsync("gone"))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        (await sut.InvokeAsync("  "))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _camps.DidNotReceive().GetSettingsAsync(Arg.Any<CancellationToken>());
    }

    private void StubSettings(int publicYear) =>
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(publicYear, [publicYear], null));

    private static CampInfo Camp(string slug, params (int Year, string Name)[] seasons)
    {
        var campId = Guid.NewGuid();
        return new CampInfo(
            campId, slug, "camp@example.org", string.Empty,
            IsSwissCamp: false, TimesAtNowhere: 0,
            Seasons: seasons.Select(s => new CampSeasonInfo(
                Guid.NewGuid(), campId, slug, s.Year, null, s.Name,
                BlurbShort: string.Empty, Languages: string.Empty, Vibes: [],
                Status: CampSeasonStatus.Active,
                AcceptingMembers: YesNoMaybe.Yes, KidsWelcome: YesNoMaybe.Yes,
                AdultPlayspace: AdultPlayspacePolicy.No, MemberCount: 0,
                SoundZone: null, SpaceRequirement: null, ElectricalGrid: null,
                EeSlotCount: 0, EeGrantedCount: null, JoinedMemberCount: null)).ToList());
    }
}
