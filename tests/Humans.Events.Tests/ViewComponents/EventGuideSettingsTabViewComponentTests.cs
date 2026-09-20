using AwesomeAssertions;
using Humans.Events.Contracts;
using Humans.Events.Models;
using Humans.Events.Services;
using Humans.Events.ViewComponents;
using Humans.Settings.Contracts;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Events.Tests.ViewComponents;

/// <summary>
/// The /Settings#event-guide tab (peterdrier/Humans#1634): mirrors what the old
/// <c>EventsAdminController.Settings</c> GET used to build.
/// </summary>
public sealed class EventGuideSettingsTabViewComponentTests
{
    private readonly IEventService _guide = Substitute.For<IEventService>();

    [HumansFact]
    public async Task InvokeAsync_NoGuideSettingsYet_ReturnsAnEmptyFormDefaultingMaxPrintSlots()
    {
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventGuideSettingsView?)null);
        _guide.GetEventSettingsOptionsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var sut = new EventGuideSettingsTabViewComponent(_guide);
        var result = await sut.InvokeAsync();
        var model = (GuideSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.Id.Should().BeNull();
        model.MaxPrintSlots.Should().Be(100);
    }

    [HumansFact]
    public async Task InvokeAsync_ExistingGuideSettings_MapsIdAndEventSettingsId()
    {
        var settingsId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(new EventGuideSettingsView(
            Id: existingId,
            EventSettingsId: settingsId,
            SubmissionOpenAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            SubmissionCloseAt: Instant.FromUtc(2026, 6, 1, 0, 0),
            GuidePublishAt: Instant.FromUtc(2026, 6, 15, 0, 0),
            MaxPrintSlots: 50,
            TimeZoneId: "Europe/Madrid",
            CreatedAt: Instant.FromUtc(2025, 1, 1, 0, 0),
            UpdatedAt: Instant.FromUtc(2025, 1, 1, 0, 0)));
        _guide.GetEventSettingsByIdAsync(settingsId, Arg.Any<CancellationToken>()).Returns(new EventSettingsInfo(
            Id: settingsId, EventName: "Test Burn", Year: 2026, TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 9), BuildStartOffset: -25, EventEndOffset: 6,
            StrikeEndOffset: 9, FirstCrewStartOffset: -25, SetupWeekStartOffset: -16,
            PreEventWeekStartOffset: -9, FinishingWeekendStartOffset: -4,
            EarlyEntryCapacity: new Dictionary<int, int>(), BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null, Status: EventSettingsStatus.Active));
        _guide.GetEventSettingsOptionsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var sut = new EventGuideSettingsTabViewComponent(_guide);
        var result = await sut.InvokeAsync();
        var model = (GuideSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.Id.Should().Be(existingId);
        model.EventSettingsId.Should().Be(settingsId);
        model.MaxPrintSlots.Should().Be(50);
        model.TimeZoneId.Should().Be("Europe/Madrid");
    }
}
