using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.ViewComponents;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

/// <summary>
/// Covers <see cref="ShiftsSearchResultViewComponent"/>: the global-search row for a rota.
/// Callers pass a rota id and nothing else (nobodies-collective/Humans#1062), so fetching
/// the rota and stitching the owning team's name is the behaviour.
/// </summary>
public class ShiftsSearchResultViewComponentTests
{
    private readonly IShiftManagementServiceRead _shifts = Substitute.For<IShiftManagementServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();

    [HumansFact]
    public async Task Fetches_the_rota_and_its_owning_teams_name()
    {
        var rotaId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _shifts.GetRotaAsync(rotaId, Arg.Any<CancellationToken>()).Returns(Rota(rotaId, teamId, "Kitchen Prep"));
        _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns(Team(teamId, "Cantina"));

        var result = await new ShiftsSearchResultViewComponent(_shifts, _teams).InvokeAsync(rotaId);

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Subject.ViewData!.Model.Should().BeOfType<ShiftsSearchResultViewModel>().Subject;
        model.Name.Should().Be("Kitchen Prep");
        model.TeamId.Should().Be(teamId);
        model.TeamName.Should().Be("Cantina");
    }

    [HumansFact]
    public async Task Still_renders_the_row_when_the_owning_team_is_gone()
    {
        // The link is by team id, so a missing team costs the subtitle, not the row.
        var rotaId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _shifts.GetRotaAsync(rotaId, Arg.Any<CancellationToken>()).Returns(Rota(rotaId, teamId, "Kitchen Prep"));
        _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns((TeamInfo?)null);

        var result = await new ShiftsSearchResultViewComponent(_shifts, _teams).InvokeAsync(rotaId);

        result.Should().BeOfType<ViewViewComponentResult>()
            .Subject.ViewData!.Model.Should().BeOfType<ShiftsSearchResultViewModel>()
            .Subject.TeamName.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Renders_nothing_for_an_unknown_rota()
    {
        _shifts.GetRotaAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((RotaInfo?)null);

        var result = await new ShiftsSearchResultViewComponent(_shifts, _teams).InvokeAsync(Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await _teams.DidNotReceive().GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static RotaInfo Rota(Guid rotaId, Guid teamId, string name) => new(
        rotaId, EventSettingsId: Guid.NewGuid(), teamId, name,
        Description: null, PracticalInfo: null,
        ShiftPriority.Normal, SignupPolicy.Public, RotaPeriod.Event,
        IsVisibleToVolunteers: true, Tags: []);

    private static TeamInfo Team(Guid teamId, string name) => new(
        teamId, name, null, name.ToLowerInvariant(),
        IsActive: true, IsSystemTeam: false, SystemTeamType: Base.Enums.SystemTeamType.None,
        RequiresApproval: false, IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
        CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0), Members: []);
}
