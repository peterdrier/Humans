using AwesomeAssertions;
using Humans.Calendar.Services.Dtos;
using Humans.Calendar.Services;
using Humans.Teams.Contracts;
using Humans.Calendar.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Calendar.Tests.Services;

public sealed class CachingCalendarServiceTests
{
    private readonly ICalendarService _inner = Substitute.For<ICalendarService>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();

    private CachingCalendarService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<ICalendarService>(
            CachingCalendarService.InnerServiceKey, (_, _) => _inner);
        services.AddScoped(_ => _teamService);

        return new CachingCalendarService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingCalendarService>.Instance);
    }

    private static Task WarmAsync(CachingCalendarService sut) =>
        ((IHostedService)sut).StartAsync(Xunit.TestContext.Current.CancellationToken);

    [HumansFact]
    public async Task DateOccurrenceCancellation_RefreshesDatesAndForwardsDateIdentity()
    {
        var day = new LocalDate(2026, 3, 29);
        var before = BuildInfo() with
        {
            IsAllDay = true, StartUtc = null, EndUtc = null,
            StartDate = day, EndDateExclusive = day.PlusDays(1),
            RecurrenceRule = "FREQ=DAILY;COUNT=1", RecurrenceTimezone = null,
        };
        var after = before with
        {
            Exceptions = [new CalendarEventExceptionInfo(Guid.NewGuid(), null, true, null, null,
                null, null, null, null, day)],
        };
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([before]);
        _inner.GetEventInfoAsync(before.Id, Arg.Any<CancellationToken>()).Returns(after);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var sut = CreateSut();
        await WarmAsync(sut);
        var detail = await sut.GetEventByIdAsync(before.Id, Xunit.TestContext.Current.CancellationToken);
        detail!.StartDate.Should().Be(day);
        detail.EndDateExclusive.Should().Be(day.PlusDays(1));
        detail.StartUtc.Should().BeNull();
        await sut.CancelOccurrenceAsync(before.Id, null, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken, day);
        await _inner.Received(1).CancelOccurrenceAsync(before.Id, null, Arg.Any<Guid>(), Arg.Any<CancellationToken>(), day);
        var occurrences = await sut.GetOccurrencesInWindowAsync(Instant.FromUtc(2026, 3, 28, 0, 0),
            Instant.FromUtc(2026, 3, 30, 0, 0), ct: Xunit.TestContext.Current.CancellationToken);
        occurrences.Should().BeEmpty();
    }

    [HumansFact]
    public async Task WarmAllAsync_LoadsCalendarEventInfosFromInnerReadSurface()
    {
        var info = BuildInfo(title: "Cached");
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>())
            .Returns([info]);

        var sut = CreateSut();

        await WarmAsync(sut);

        sut.Entries.Should().Be(1);
        sut.ContainsKey(info.Id).Should().BeTrue();
    }

    [HumansFact]
    public async Task GetEventByIdAsync_AfterWarmup_AnswersFromCache()
    {
        var info = BuildInfo(title: "Cached event");
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>())
            .Returns([info]);

        var sut = CreateSut();
        await WarmAsync(sut);

        var detail = await sut.GetEventByIdAsync(info.Id, Xunit.TestContext.Current.CancellationToken);

        detail.Should().NotBeNull();
        detail.Id.Should().Be(info.Id);
        detail.Title.Should().Be("Cached event");
    }

    [HumansFact]
    public async Task GetOccurrencesInWindowAsync_AnswersFromCachedReadModel()
    {
        var teamId = Guid.NewGuid();
        var inWindow = BuildInfo(
            teamId: teamId,
            title: "In window",
            start: Instant.FromUtc(2026, 6, 5, 10, 0),
            end: Instant.FromUtc(2026, 6, 5, 11, 0));
        var outsideWindow = BuildInfo(
            teamId: teamId,
            title: "Outside window",
            start: Instant.FromUtc(2027, 1, 1, 0, 0),
            end: Instant.FromUtc(2027, 1, 1, 1, 0));
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>())
            .Returns([inWindow, outsideWindow]);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        var sut = CreateSut();

        var results = await sut.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            Instant.FromUtc(2026, 6, 30, 0, 0), ct: Xunit.TestContext.Current.CancellationToken);

        results.Should().ContainSingle();
        results[0].EventId.Should().Be(inWindow.Id);
    }

    [HumansFact]
    public async Task CreateEventWithResultAsync_DelegatesToInnerAndRefreshesEntry()
    {
        var created = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "Created",
            OwningTeamId = Guid.NewGuid(),
            StartUtc = Instant.FromUtc(2026, 7, 1, 9, 0),
            EndUtc = Instant.FromUtc(2026, 7, 1, 10, 0),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Instant.FromUtc(2026, 7, 1, 8, 0),
            UpdatedAt = Instant.FromUtc(2026, 7, 1, 8, 0),
        };
        var dto = new CreateCalendarEventDto(
            created.Title, null, null, null, created.OwningTeamId,
            created.StartUtc, created.EndUtc, false, null, null);
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _inner.CreateEventWithResultAsync(dto, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEventMutationResult.Success(created));
        _inner.GetEventInfoAsync(created.Id, Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceExpander.ToInfo(created));

        var sut = CreateSut();
        await WarmAsync(sut);

        await sut.CreateEventWithResultAsync(dto, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        sut.ContainsKey(created.Id).Should().BeTrue();
    }

    // Invariant: a per-occurrence write has no cache row of its own, so it must evict and
    // reload the PARENT event. Without the ReplaceAsync(eventId) in the decorator, every
    // read serves the pre-cancel series until the process restarts.
    [HumansFact]
    public async Task CancelOccurrenceAsync_RefreshesTheParentEventEntry()
    {
        var before = BuildInfo(title: "Weekly standup");
        var after = before with { Title = "Weekly standup (one cancelled)" };
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([before]);
        _inner.GetEventInfoAsync(before.Id, Arg.Any<CancellationToken>()).Returns(after);

        var sut = CreateSut();
        await WarmAsync(sut);

        var occurrence = Instant.FromUtc(2026, 6, 8, 10, 0);
        await sut.CancelOccurrenceAsync(before.Id, occurrence, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _inner.Received(1).CancelOccurrenceAsync(
            before.Id, occurrence, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        var reloaded = await sut.GetEventByIdAsync(before.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded!.Title.Should().Be(after.Title, because: "the parent entry is reloaded after a per-occurrence write");
    }

    [HumansFact]
    public async Task OverrideOccurrenceAsync_RefreshesTheParentEventEntry()
    {
        var before = BuildInfo(title: "Weekly standup");
        var after = before with { Title = "Weekly standup (one moved)" };
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([before]);
        _inner.GetEventInfoAsync(before.Id, Arg.Any<CancellationToken>()).Returns(after);

        var sut = CreateSut();
        await WarmAsync(sut);

        var occurrence = Instant.FromUtc(2026, 6, 8, 10, 0);
        var dto = new OverrideOccurrenceDto(
            Instant.FromUtc(2026, 6, 8, 14, 0), Instant.FromUtc(2026, 6, 8, 15, 0),
            null, null, null, null);
        await sut.OverrideOccurrenceAsync(before.Id, occurrence, dto, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _inner.Received(1).OverrideOccurrenceAsync(
            before.Id, occurrence, dto, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        var reloaded = await sut.GetEventByIdAsync(before.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded!.Title.Should().Be(after.Title, because: "the parent entry is reloaded after a per-occurrence write");
    }

    [HumansFact]
    public async Task DeleteEventAsync_TombstonesMissingEntry()
    {
        var info = BuildInfo(title: "Deleted");
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>())
            .Returns([info]);
        _inner.GetEventInfoAsync(info.Id, Arg.Any<CancellationToken>())
            .Returns((CalendarEventInfo?)null);

        var sut = CreateSut();
        await WarmAsync(sut);

        await sut.DeleteEventAsync(info.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        sut.ContainsKey(info.Id).Should().BeFalse();
        await _inner.Received(1).DeleteEventAsync(info.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static CalendarEventInfo BuildInfo(
        Guid? id = null,
        Guid? teamId = null,
        string title = "Test",
        Instant? start = null,
        Instant? end = null) => new(
            Id: id ?? Guid.NewGuid(),
            Title: title,
            Description: null,
            Location: null,
            LocationUrl: null,
            OwningTeamId: teamId ?? Guid.NewGuid(),
            StartUtc: start ?? Instant.FromUtc(2026, 6, 1, 10, 0),
            EndUtc: end ?? Instant.FromUtc(2026, 6, 1, 11, 0),
            IsAllDay: false,
            RecurrenceRule: null,
            RecurrenceTimezone: null,
            RecurrenceUntilUtc: null,
            CreatedByUserId: Guid.NewGuid(),
            CreatedAt: Instant.FromUtc(2026, 5, 1, 0, 0),
            UpdatedAt: Instant.FromUtc(2026, 5, 1, 0, 0),
            Exceptions: []);
}
