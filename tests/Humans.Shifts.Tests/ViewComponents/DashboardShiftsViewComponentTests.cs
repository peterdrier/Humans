using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Shifts.Services;

namespace Humans.Shifts.Tests.ViewComponents;

public sealed class DashboardShiftsViewComponentTests
{
    private readonly IShiftManagement _shiftMgmt = Substitute.For<IShiftManagement>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly Guid _userId = Guid.NewGuid();

    private static readonly BurnSettingsInfo OpenBurn = new(
        Id: Guid.NewGuid(),
        EventName: "Test Event",
        Year: 2026,
        TimeZoneId: "UTC",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -14,
        SetupWeekStartOffset: -10,
        PreEventWeekStartOffset: -7,
        FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    private static EventSettingsInfo Event(bool isOffSeason) => new(
        Guid.NewGuid(), "Test Event", 2026, "UTC", new LocalDate(2026, 7, 1),
        -14, 6, 9, new Dictionary<int, int>(), null, null,
        IsOffSeason: isOffSeason);

    private static TeamInfo VolunteersTeam(params Guid[] memberIds) => new(
        SystemTeamIds.Volunteers, "Volunteers", null, "volunteers",
        IsActive: true, IsSystemTeam: true, SystemTeamType.Volunteers, RequiresApproval: false,
        IsPublicPage: false, IsHidden: false, IsPromotedToDirectory: false, Instant.MinValue,
        memberIds.Select(id => new TeamMemberInfo(
            Guid.NewGuid(), id, "Member", null, null, TeamMemberRole.Member, Instant.MinValue)).ToList());

    private DashboardShiftsViewComponent BuildSut()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "test"));
        return new DashboardShiftsViewComponent(
            _shiftMgmt, _shiftView, _burnSettings, _settings, _teams,
            SystemClock.Instance, NullLogger<DashboardShiftsViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext },
            },
        };
    }

    private async Task<DashboardShiftsViewModel> InvokeAsync(bool isOffSeason)
    {
        _teams.GetTeamAsync(SystemTeamIds.Volunteers, Arg.Any<CancellationToken>()).Returns(VolunteersTeam(_userId));
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(OpenBurn);
        _settings.GetActiveEventSettings().Returns(Event(isOffSeason));
        _shiftMgmt.GetUrgentShiftsAsync(OpenBurn.Id, 3).Returns([]);

        var result = await BuildSut().InvokeAsync();
        return (DashboardShiftsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
    }

    [HumansFact]
    public async Task InvokeAsync_AbortedUrgencyReadPropagatesInsteadOfRenderingAnEmptyCard()
    {
        using var cancellation = new CancellationTokenSource();
        var sut = BuildSut();
        sut.HttpContext.RequestAborted = cancellation.Token;
        _teams.GetTeamAsync(SystemTeamIds.Volunteers, Arg.Any<CancellationToken>()).Returns(VolunteersTeam(_userId));
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(OpenBurn);
        _settings.GetActiveEventSettings().Returns(Event(false));
        _shiftMgmt.GetUrgentShiftsAsync(OpenBurn.Id, limit: 3, ct: cancellation.Token)
            .Returns(async _ =>
            {
                await cancellation.CancelAsync();
                return await Task.FromCanceled<IReadOnlyList<UrgentShiftInfo>>(cancellation.Token);
            });

        Func<Task> invoke = () => sut.InvokeAsync();

        await invoke.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task InvokeAsync_InSeason_ShiftBrowsingFollowsTheBurnSwitch()
    {
        var model = await InvokeAsync(isOffSeason: false);

        model.IsOffSeason.Should().BeFalse();
        model.IsShiftBrowsingOpen.Should().BeTrue();
    }

    [HumansFact]
    public async Task InvokeAsync_OffSeason_ClosesShiftBrowsingEvenWhenTheBurnSwitchIsOpen()
    {
        var model = await InvokeAsync(isOffSeason: true);

        model.IsOffSeason.Should().BeTrue();
        model.IsShiftBrowsingOpen.Should().BeFalse();
        await _shiftMgmt.DidNotReceive().GetUrgentShiftsAsync(Arg.Any<Guid>(), Arg.Any<int>());
    }
}
