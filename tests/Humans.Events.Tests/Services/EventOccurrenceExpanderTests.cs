using AwesomeAssertions;
using Humans.Events.Services;
using NodaTime;
using Humans.Events.Contracts;

namespace Humans.Events.Tests;

public sealed class EventOccurrenceExpanderTests
{
    private static readonly DateTimeZone Tz = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly LocalDate GateDate = new(2026, 7, 5);
    private static readonly ILookup<Guid, int?> EmptyFavourites =
        Array.Empty<(Guid, int?)>().ToLookup(f => f.Item1, f => f.Item2);

    [HumansFact]
    public void NonRecurringEvent_NoGateDate_YieldsSingleOccurrenceWithZeroDayOffset()
    {
        var startAt = Instant.FromUtc(2026, 7, 8, 10, 0);
        var e = Approved(startAt, isRecurring: false);

        var result = EventOccurrenceExpander.Expand(
            [e], EmptyFavourites, gateOpeningDate: null, timeZone: null, filterDays: null);

        result.Should().ContainSingle();
        result[0].StartAt.Should().Be(startAt);
        result[0].DayOffset.Should().Be(0);
        result[0].FavouriteDayOffset.Should().BeNull();
    }

    [HumansFact]
    public void RecurringEvent_ExpandsToOneItemPerMatchingDay_WithGateRelativeDayOffsets()
    {
        var startAt = new LocalDateTime(2026, 7, 12, 18, 30).InZoneStrictly(Tz).ToInstant();
        var e = Approved(startAt, isRecurring: true, recurrenceDays: "2,3");

        var result = EventOccurrenceExpander.Expand(
            [e], EmptyFavourites, GateDate, Tz, filterDays: null);

        result.Should().HaveCount(2);
        result.Select(o => o.DayOffset).Should().Equal(2, 3);
        // Recurring events favourite per-occurrence.
        result.Select(o => o.FavouriteDayOffset).Should().Equal(2, 3);
    }

    [HumansFact]
    public void DayFilter_ExcludesOccurrencesOutsideTheFilterSet()
    {
        var startAt = new LocalDateTime(2026, 7, 12, 18, 30).InZoneStrictly(Tz).ToInstant();
        var e = Approved(startAt, isRecurring: true, recurrenceDays: "0,1,2");

        var result = EventOccurrenceExpander.Expand(
            [e], EmptyFavourites, GateDate, Tz, filterDays: new HashSet<int> { 1 });

        result.Should().ContainSingle();
        result[0].DayOffset.Should().Be(1);
    }

    [HumansFact]
    public void NonRecurringEvent_FavouriteDayOffsetIsAlwaysNull_EvenWithGateDateAndTimeZone()
    {
        var startAt = new LocalDateTime(2026, 7, 8, 10, 0).InZoneStrictly(Tz).ToInstant();
        var e = Approved(startAt, isRecurring: false);

        var result = EventOccurrenceExpander.Expand(
            [e], EmptyFavourites, GateDate, Tz, filterDays: null);

        result.Should().ContainSingle();
        result[0].FavouriteDayOffset.Should().BeNull();
    }

    [HumansFact]
    public void IsFavourited_WholeEventFavourite_MatchesEveryOccurrence()
    {
        var startAt = new LocalDateTime(2026, 7, 12, 18, 30).InZoneStrictly(Tz).ToInstant();
        var e = Approved(startAt, isRecurring: true, recurrenceDays: "0,1");
        var favourites = new[] { (e.Id, (int?)null) }.ToLookup(f => f.Item1, f => f.Item2);

        var result = EventOccurrenceExpander.Expand([e], favourites, GateDate, Tz, filterDays: null);

        result.Should().OnlyContain(o => o.IsFavourited);
    }

    [HumansFact]
    public void IsFavourited_SpecificDayFavourite_MatchesOnlyThatOccurrence()
    {
        var startAt = new LocalDateTime(2026, 7, 12, 18, 30).InZoneStrictly(Tz).ToInstant();
        var e = Approved(startAt, isRecurring: true, recurrenceDays: "0,1");
        var favourites = new[] { (e.Id, (int?)1) }.ToLookup(f => f.Item1, f => f.Item2);

        var result = EventOccurrenceExpander.Expand([e], favourites, GateDate, Tz, filterDays: null);

        result.Single(o => o.DayOffset == 0).IsFavourited.Should().BeFalse();
        result.Single(o => o.DayOffset == 1).IsFavourited.Should().BeTrue();
    }

    private static ApprovedEventView Approved(
        Instant startAt, bool isRecurring, string? recurrenceDays = null) => new(
        Id: Guid.NewGuid(), CampId: null, GuideSharedVenueId: Guid.NewGuid(), SubmitterUserId: Guid.NewGuid(),
        CategoryId: Guid.NewGuid(), CategorySlug: "music", CategoryName: "Music", CategoryIsSensitive: false,
        VenueName: null, Title: "Test Event", Description: "", LocationNote: null, Host: null,
        StartAt: startAt, DurationMinutes: 60, IsRecurring: isRecurring, RecurrenceDays: recurrenceDays,
        PriorityRank: 0, SubmittedAt: startAt, LastUpdatedAt: startAt);
}
