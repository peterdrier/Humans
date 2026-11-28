using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;
using Humans.Shifts.Services;

namespace Humans.Shifts.Tests.ViewComponents;

/// <summary>
/// It takes the event by id and resolves Shifts' own <see cref="BurnSettingsInfo"/>: handing it Settings'
/// <c>EventSettingsInfo</c> threw an InvalidCastException at render.
/// </summary>
public sealed class OnboardingShiftsListViewComponentTests
{
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftService _shifts = Substitute.For<IShiftService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly Guid _viewer = Guid.NewGuid();
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
        _sut = new OnboardingShiftsListViewComponent(_burnSettings, _shifts, _shiftView)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
        _shiftView.GetUserAsync(_viewer, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserSummary>(ShiftUserSummary.Empty(_viewer)));
    }

    private Task<IViewComponentResult> InvokeAsync(Guid eventId, UrgentShiftInfo[] rows, IReadOnlyList<Guid>? shown = null)
    {
        _shifts.GetBrowseShiftsAsync(
                Arg.Is<ShiftBrowseQuery>(q => q.EventSettingsId == eventId && q.ViewerUserId == _viewer),
                Arg.Any<CancellationToken>())
            .Returns(rows);
        return _sut.InvokeAsync(eventId, _viewer, shown ?? rows.Select(u => u.Shift.Id).ToList());
    }

    [HumansFact]
    public async Task Invoke_EventId_ResolvesBurnSettings()
    {
        _burnSettings.GetByIdAsync(Event.Id, Arg.Any<CancellationToken>()).Returns(Event);

        var locked = UrgentShiftFixtures.Urgent() with { EarlyEntrySignupsClosed = true };
        var open = UrgentShiftFixtures.Urgent() with { EarlyEntrySignupsClosed = false };
        var result = await InvokeAsync(Event.Id, [locked, open]);

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Which.ViewData!.Model.Should().BeOfType<ShiftBrowseViewModel>().Subject;
        model.EventSettings.Should().BeSameAs(Event);
        model.UrgencyRankedRotas.Single(r => r.Rota.Id == locked.Rota.Id).EarlyEntrySignupsClosed.Should().BeTrue();
        model.UrgencyRankedRotas.Single(r => r.Rota.Id == open.Rota.Id).EarlyEntrySignupsClosed.Should().BeFalse();
    }

    [HumansFact]
    public async Task Invoke_UnorderedRows_KeepRankedTies()
    {
        _burnSettings.GetByIdAsync(Event.Id, Arg.Any<CancellationToken>()).Returns(Event);
        var low = UrgentShiftFixtures.Urgent(urgencyScore: 1);
        var other = UrgentShiftFixtures.Urgent(urgencyScore: 10);
        var high = UrgentShiftFixtures.Urgent(
            shift: UrgentShiftFixtures.Shift(rotaId: low.Rota.Id), rota: low.Rota, urgencyScore: 10);

        var result = await InvokeAsync(Event.Id, [low, other, high]);

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Which.ViewData!.Model.Should().BeOfType<ShiftBrowseViewModel>().Subject;
        model.UrgencyRankedRotas.Select(r => r.Rota.Id).Should().Equal(other.Rota.Id, low.Rota.Id);
        model.UrgencyRankedRotas[1].Shifts.Select(s => s.Shift.Id).Should().Equal(high.Shift.Id, low.Shift.Id);
    }

    [HumansFact]
    public async Task Invoke_PassedShifts_ShownWithViewerSignups()
    {
        _burnSettings.GetByIdAsync(Event.Id, Arg.Any<CancellationToken>()).Returns(Event);
        var shown = UrgentShiftFixtures.Urgent();
        var hidden = UrgentShiftFixtures.Urgent();
        _shiftView.GetUserAsync(_viewer, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserSummary>(ShiftFixtures.UserSummary(_viewer,
                [ShiftFixtures.Signup(shiftId: shown.Shift.Id, status: SignupStatus.Pending)])));

        var result = await InvokeAsync(Event.Id, [shown, hidden], [shown.Shift.Id]);

        var model = result.Should().BeOfType<ViewViewComponentResult>()
            .Which.ViewData!.Model.Should().BeOfType<ShiftBrowseViewModel>().Subject;
        model.UrgencyRankedRotas.Select(r => r.Rota.Id).Should().Equal(shown.Rota.Id);
        model.UserSignupStatuses[shown.Shift.Id].Should().Be(SignupStatus.Pending);
    }

    [HumansFact]
    public async Task Invoke_UnknownEvent_RendersNothing()
    {
        var result = await InvokeAsync(Guid.NewGuid(), []);

        result.Should().BeOfType<ContentViewComponentResult>()
            .Which.Content.Should().BeEmpty();
    }
}
