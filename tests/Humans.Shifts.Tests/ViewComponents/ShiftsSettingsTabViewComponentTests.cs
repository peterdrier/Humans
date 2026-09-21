using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

/// <summary>The /Settings#shifts tab (peterdrier/Humans#1634).</summary>
public sealed class ShiftsSettingsTabViewComponentTests
{
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly ShiftsSettingsTabViewComponent _sut;

    private static readonly BurnSettingsInfo ActiveEvent = new(
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

    public ShiftsSettingsTabViewComponentTests()
    {
        _sut = new ShiftsSettingsTabViewComponent(_burnSettings, _shiftMgmt)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
    }

    [HumansFact]
    public async Task InvokeAsync_ActiveEventWithKnobs_ReturnsTheKnobs()
    {
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ActiveEvent);
        _shiftMgmt.GetKnobsAsync(ActiveEvent.Id).Returns(new ShiftEventKnobs(false, 42, 6));

        var result = await _sut.InvokeAsync();
        var model = (EventSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.IsShiftBrowsingOpen.Should().BeFalse();
        model.GlobalVolunteerCap.Should().Be(42);
        model.ReminderLeadTimeHours.Should().Be(6);
    }

    [HumansFact]
    public async Task InvokeAsync_NoActiveEvent_ReturnsABlankForm()
    {
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);

        var result = await _sut.InvokeAsync();
        var model = (EventSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.IsShiftBrowsingOpen.Should().BeFalse();
        model.GlobalVolunteerCap.Should().BeNull();
        await _shiftMgmt.DidNotReceive().GetKnobsAsync(Arg.Any<Guid>());
    }
}
