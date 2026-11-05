using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

/// <summary>
/// Onboarding invokes this component by name, so its arguments are unchecked at compile
/// time. It takes the event by id and resolves Shifts' own <see cref="BurnSettingsInfo"/>:
/// handing it Settings' <c>EventSettingsInfo</c> threw an InvalidCastException at render.
/// </summary>
public sealed class OnboardingShiftsListViewComponentTests
{
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly OnboardingShiftsListViewComponent _sut;

    private static readonly BurnSettingsInfo Event = new(
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

    public OnboardingShiftsListViewComponentTests()
    {
        _sut = new OnboardingShiftsListViewComponent(_burnSettings)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
    }

    [HumansFact]
    public async Task Renders_with_the_burn_settings_resolved_from_the_id()
    {
        _burnSettings.GetByIdAsync(Event.Id, Arg.Any<CancellationToken>()).Returns(Event);

        var locked = UrgentShiftFixtures.Urgent() with { EarlyEntrySignupsClosed = true };
        var open = UrgentShiftFixtures.Urgent() with { EarlyEntrySignupsClosed = false };
        var result = await _sut.InvokeAsync(Event.Id, [locked, open], [], new Dictionary<Guid, SignupStatus>());

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Which.ViewData!.Model.Should().BeOfType<ShiftBrowseViewModel>().Subject;
        model.EventSettings.Should().BeSameAs(Event);
        model.UrgencyRankedRotas.Single(r => r.Rota.Id == locked.Rota.Id).EarlyEntrySignupsClosed.Should().BeTrue();
        model.UrgencyRankedRotas.Single(r => r.Rota.Id == open.Rota.Id).EarlyEntrySignupsClosed.Should().BeFalse();
    }

    [HumansFact]
    public async Task Renders_nothing_when_the_event_is_unknown()
    {
        var result = await _sut.InvokeAsync(Guid.NewGuid(), [], [], new Dictionary<Guid, SignupStatus>());

        result.Should().BeOfType<ContentViewComponentResult>()
            .Which.Content.Should().BeEmpty();
    }
}
