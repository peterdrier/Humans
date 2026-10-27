using System.Security.Claims;
using AwesomeAssertions;
using Humans.Governance.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

public sealed class DashboardShiftsViewComponentTests
{
    private readonly IShiftService _shiftMgmt = Substitute.For<IShiftService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMembershipCalculator _membership = Substitute.For<IMembershipCalculator>();

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
        -14, 6, 9, -14, -10, -7, -3, new Dictionary<int, int>(), null, null,
        IsOffSeason: isOffSeason);

    private DashboardShiftsViewComponent BuildSut()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        return new DashboardShiftsViewComponent(
            _shiftMgmt, _shiftView, _burnSettings, _settings, _membership,
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
        _membership.GetMembershipSnapshotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Active, true, 0, 0, []));
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(OpenBurn);
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(Event(isOffSeason));
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
        _membership.GetMembershipSnapshotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Active, true, 0, 0, []));
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(OpenBurn);
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(Event(false));
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
