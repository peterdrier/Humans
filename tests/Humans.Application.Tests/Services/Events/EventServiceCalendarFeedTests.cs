using AwesomeAssertions;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Events;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Events;

public class EventServiceCalendarFeedTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 6, 15, 12, 0);
    // 19:00 Europe/Madrid on 2026-07-01 = 17:00 UTC (CEST).
    private static readonly Instant EventStart = Instant.FromUtc(2026, 7, 1, 17, 0);

    private readonly IEventRepository _repo = Substitute.For<IEventRepository>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly EventService _service;

    public EventServiceCalendarFeedTests()
    {
        _service = new EventService(_repo, _burnSettings, Substitute.For<IUserServiceRead>(), Substitute.For<IEmailService>(), Substitute.For<IEmailMessageFactory>(), new FakeClock(FixedNow), NullLogger<EventService>.Instance);
        // Default: no guide settings → no recurrence expansion context.
        _repo.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventGuideSettings?)null);
    }

    private static Event MakeEvent(
        EventStatus status,
        bool isRecurring = false,
        string? recurrenceDays = null) => new()
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            Title = "Sunset Yoga",
            Description = "Bring a mat.",
            LocationNote = "Behind the dome",
            Host = "Stretchy",
            StartAt = EventStart,
            DurationMinutes = 90,
            IsRecurring = isRecurring,
            RecurrenceDays = recurrenceDays,
            Status = status,
            SubmittedAt = FixedNow,
            LastUpdatedAt = FixedNow,
        };

    private void StubFavourites(Guid userId, params Event[] events) =>
        StubFavourites(userId, dayOffset: null, events);

    private void StubFavourites(Guid userId, int? dayOffset, params Event[] events)
    {
        IReadOnlyList<EventFavourite> favourites = events.Select(e => new EventFavourite
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GuideEventId = e.Id,
            DayOffset = dayOffset,
            CreatedAt = FixedNow,
            Event = e,
        }).ToList();
        _repo.GetFavouritesWithEventsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(favourites);
    }

    private void StubBurnSettings()
    {
        var guideSettings = new EventGuideSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = Guid.NewGuid(),
            SubmissionOpenAt = FixedNow,
            SubmissionCloseAt = FixedNow,
            GuidePublishAt = FixedNow,
            MaxPrintSlots = 3,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        };
        _repo.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(guideSettings);
        _burnSettings.GetByIdAsync(guideSettings.EventSettingsId, Arg.Any<CancellationToken>())
            .Returns(new BurnSettingsInfo(
                Id: guideSettings.EventSettingsId,
                EventName: "Test Event 2026",
                Year: 2026,
                TimeZoneId: "Europe/Madrid",
                GateOpeningDate: new LocalDate(2026, 7, 1),
                BuildStartOffset: -14,
                EventEndOffset: 6,
                StrikeEndOffset: 9,
                FirstCrewStartOffset: -14,
                SetupWeekStartOffset: -7,
                PreEventWeekStartOffset: -3,
                FinishingWeekendStartOffset: -2,
                EarlyEntryCapacity: new Dictionary<int, int>(),
                BarriosEarlyEntryAllocation: null,
                EarlyEntryClose: null));
    }

    [HumansFact]
    public async Task GetCalendarItems_ApprovedFavourite_MapsFields()
    {
        var userId = Guid.NewGuid();
        var ev = MakeEvent(EventStatus.Approved);
        StubFavourites(userId, ev);

        var items = await _service.GetCalendarItemsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        items.Should().HaveCount(1);
        var item = items[0];
        item.Source.Should().Be("Events");
        item.Summary.Should().Be("Sunset Yoga");
        item.Start.Should().Be(EventStart);
        item.End.Should().Be(EventStart.Plus(Duration.FromMinutes(90)));
        item.Location.Should().Be("Behind the dome");
        item.Description.Should().Contain("Bring a mat.");
        item.Description.Should().Contain("Host: Stretchy");
        item.Uid.Should().Be($"event-{ev.Id}-20260701@humans.nobodies.team");
        item.Url.Should().Be("https://humans.nobodies.team/Events/Schedule");
    }

    [HumansFact]
    public async Task GetCalendarItems_NonApprovedFavourites_Excluded()
    {
        var userId = Guid.NewGuid();
        StubFavourites(
            userId,
            MakeEvent(EventStatus.Pending),
            MakeEvent(EventStatus.Rejected),
            MakeEvent(EventStatus.Withdrawn));

        var items = await _service.GetCalendarItemsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCalendarItems_RecurringEvent_OneItemPerOccurrence()
    {
        var userId = Guid.NewGuid();
        StubBurnSettings();
        // Day offsets 0, 2 from gate opening 2026-07-01 → July 1 and July 3.
        var ev = MakeEvent(EventStatus.Approved, isRecurring: true, recurrenceDays: "0,2");
        StubFavourites(userId, ev);

        var items = await _service.GetCalendarItemsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        items.Should().HaveCount(2);
        items.Select(i => i.Start).Should().BeEquivalentTo(new[]
        {
            Instant.FromUtc(2026, 7, 1, 17, 0),
            Instant.FromUtc(2026, 7, 3, 17, 0),
        });
        items.Select(i => i.Uid).Should().BeEquivalentTo(new[]
        {
            $"event-{ev.Id}-20260701@humans.nobodies.team",
            $"event-{ev.Id}-20260703@humans.nobodies.team",
        });
    }

    [HumansFact]
    public async Task GetCalendarItems_DaySpecificFavourite_OnlyThatOccurrence()
    {
        var userId = Guid.NewGuid();
        StubBurnSettings();
        // Recurs on day offsets 0 and 2; the favourite picks only day 2 → July 3.
        var ev = MakeEvent(EventStatus.Approved, isRecurring: true, recurrenceDays: "0,2");
        StubFavourites(userId, dayOffset: 2, ev);

        var items = await _service.GetCalendarItemsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        items.Should().HaveCount(1);
        items[0].Start.Should().Be(Instant.FromUtc(2026, 7, 3, 17, 0));
        items[0].Uid.Should().Be($"event-{ev.Id}-20260703@humans.nobodies.team");
    }

    [HumansFact]
    public async Task GetCalendarItems_RecurringWithoutGuideSettings_FallsBackToSingleOccurrence()
    {
        var userId = Guid.NewGuid();
        var ev = MakeEvent(EventStatus.Approved, isRecurring: true, recurrenceDays: "0,2");
        StubFavourites(userId, ev);

        var items = await _service.GetCalendarItemsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        items.Should().HaveCount(1);
        items[0].Start.Should().Be(EventStart);
    }

    [HumansFact]
    public async Task GetCalendarItems_NoFavourites_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        _repo.GetFavouritesWithEventsAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<EventFavourite>)[]);

        var items = await _service.GetCalendarItemsForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }
}
