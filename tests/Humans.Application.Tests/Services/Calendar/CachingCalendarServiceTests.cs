using AwesomeAssertions;
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Calendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Calendar;

/// <summary>
/// Tests for <see cref="CachingCalendarService"/> — the §15 Singleton
/// decorator over a keyed Scoped inner <see cref="ICalendarService"/>.
/// Cache-migration plan task T-08.
/// </summary>
/// <remarks>
/// Covers:
/// <list type="bullet">
///   <item>Warmup populates the dict from <c>ICalendarRepository.GetAllAsync</c>.</item>
///   <item><c>GetEventByIdAsync</c> hits the cache after warmup (does not call
///     the inner service).</item>
///   <item><c>GetOccurrencesInWindowAsync</c> answers from the cache snapshot
///     (does not call the inner service after warmup).</item>
///   <item>Write paths invalidate via re-fetch from the repository.</item>
///   <item>Exception writes (cancel / override) evict the PARENT event entry.</item>
///   <item>Soft-deleted events are removed from the cache on invalidation.</item>
/// </list>
/// </remarks>
public class CachingCalendarServiceTests
{
    private readonly ICalendarRepository _repo = Substitute.For<ICalendarRepository>();
    private readonly ICalendarService _inner = Substitute.For<ICalendarService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();

    private CachingCalendarService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<ICalendarService>(
            CachingCalendarService.InnerServiceKey, (_, _) => _inner);
        services.AddScoped(_ => _teamService);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new CachingCalendarService(
            _repo,
            scopeFactory,
            NullLogger<CachingCalendarService>.Instance);
    }

    private static CalendarEvent BuildEvent(
        Guid? id = null,
        Guid? teamId = null,
        string title = "Test event",
        Instant? start = null,
        Instant? end = null,
        bool isAllDay = false,
        params CalendarEventException[] exceptions)
    {
        var s = start ?? Instant.FromUtc(2026, 6, 1, 10, 0);
        return new CalendarEvent
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            OwningTeamId = teamId ?? Guid.NewGuid(),
            StartUtc = s,
            EndUtc = end ?? s.Plus(NodaTime.Duration.FromHours(1)),
            IsAllDay = isAllDay,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = s,
            UpdatedAt = s,
            Exceptions = exceptions.ToList(),
        };
    }

    [HumansFact]
    public async Task WarmAllAsync_LoadsAllEventsFromRepo_IntoCache()
    {
        var e1 = BuildEvent(title: "Event 1");
        var e2 = BuildEvent(title: "Event 2");
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent> { e1, e2 });

        var sut = CreateSut();
        await sut.WarmAllAsync();

        sut.IsWarmedUp.Should().BeTrue();
        sut.Entries.Should().Be(2);
        sut.ContainsKey(e1.Id).Should().BeTrue();
        sut.ContainsKey(e2.Id).Should().BeTrue();
    }

    [HumansFact]
    public async Task GetEventByIdAsync_AfterWarmup_DoesNotHitInner()
    {
        var ev = BuildEvent(title: "Cached event");
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent> { ev });

        var sut = CreateSut();

        var detail = await sut.GetEventByIdAsync(ev.Id);

        detail.Should().NotBeNull();
        detail!.Id.Should().Be(ev.Id);
        detail.Title.Should().Be("Cached event");
        // Hot path: cache hit — inner is not consulted.
        await _inner.DidNotReceive().GetEventByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetEventByIdAsync_NotInCache_FallsThroughToInner()
    {
        // Empty warmup
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent>());

        var unknownId = Guid.NewGuid();
        _inner.GetEventByIdAsync(unknownId, Arg.Any<CancellationToken>())
            .Returns((CalendarEventDetail?)null);

        var sut = CreateSut();
        var result = await sut.GetEventByIdAsync(unknownId);

        result.Should().BeNull();
        await _inner.Received(1).GetEventByIdAsync(unknownId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetOccurrencesInWindowAsync_AnswersFromCacheSnapshot_NotInner()
    {
        var teamId = Guid.NewGuid();
        var inWindow = BuildEvent(
            teamId: teamId,
            title: "In window",
            start: Instant.FromUtc(2026, 6, 5, 10, 0),
            end: Instant.FromUtc(2026, 6, 5, 11, 0));
        var outsideWindow = BuildEvent(
            teamId: teamId,
            title: "Outside window",
            start: Instant.FromUtc(2027, 1, 1, 0, 0),
            end: Instant.FromUtc(2027, 1, 1, 1, 0));

        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent> { inWindow, outsideWindow });

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, Application.Interfaces.Teams.TeamInfo>)
                new Dictionary<Guid, Application.Interfaces.Teams.TeamInfo>());

        var sut = CreateSut();

        var results = await sut.GetOccurrencesInWindowAsync(
            from: Instant.FromUtc(2026, 6, 1, 0, 0),
            to: Instant.FromUtc(2026, 6, 30, 0, 0));

        results.Should().HaveCount(1);
        results[0].EventId.Should().Be(inWindow.Id);
        results[0].Title.Should().Be("In window");
        // Window queries are answered from the cache snapshot — inner is not consulted.
        await _inner.DidNotReceive().GetOccurrencesInWindowAsync(
            Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateEventAsync_DelegatesToInner_AndRefreshesCacheEntry()
    {
        var teamId = Guid.NewGuid();
        var dto = new CreateCalendarEventDto(
            "New", null, null, null, teamId,
            Instant.FromUtc(2026, 7, 1, 9, 0),
            Instant.FromUtc(2026, 7, 1, 10, 0),
            false, null, null);

        // Empty warmup so the cache starts empty.
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent>());

        var created = BuildEvent(teamId: teamId, title: "New",
            start: dto.StartUtc, end: dto.EndUtc);
        _inner.CreateEventAsync(dto, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(created);

        // After-write refresh: the decorator re-fetches the affected event
        // from the repo and upserts it into the dict.
        _repo.GetEventByIdAsync(created.Id, Arg.Any<CancellationToken>())
            .Returns(created);

        var sut = CreateSut();
        var result = await sut.CreateEventAsync(dto, Guid.NewGuid());

        result.Should().BeSameAs(created);
        sut.ContainsKey(created.Id).Should().BeTrue(
            because: "after-write refresh inserts the new event into the cache");
        await _repo.Received(1).GetEventByIdAsync(created.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteEventAsync_RemovesEntryFromCache_WhenRepoReturnsNull()
    {
        var ev = BuildEvent(title: "To be deleted");
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent> { ev });

        // The inner soft-deletes; the post-delete re-fetch returns null
        // because the repo's GetEventByIdAsync filters soft-deleted rows.
        _repo.GetEventByIdAsync(ev.Id, Arg.Any<CancellationToken>())
            .Returns((CalendarEvent?)null);

        var sut = CreateSut();
        await sut.WarmAllAsync();
        sut.ContainsKey(ev.Id).Should().BeTrue();

        await sut.DeleteEventAsync(ev.Id, Guid.NewGuid());

        sut.ContainsKey(ev.Id).Should().BeFalse(
            because: "soft-deleted event must be evicted from cache");
        await _inner.Received(1).DeleteEventAsync(ev.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CancelOccurrenceAsync_EvictsParentEventEntry_NotJustException()
    {
        // T-08 invariant: the cache is keyed by parent event id; exception
        // writes refresh the PARENT entry (which re-loads its Exceptions list).
        var eventId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var originalStart = Instant.FromUtc(2026, 8, 1, 10, 0);

        // Pre-warm with the event having NO exceptions.
        var before = BuildEvent(
            id: eventId, teamId: teamId, title: "Recurring",
            start: Instant.FromUtc(2026, 8, 1, 10, 0));
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent> { before });

        // After cancel: the repo returns the same event but now with a
        // cancelled exception attached. The cache must reflect this.
        var after = BuildEvent(
            id: eventId, teamId: teamId, title: "Recurring",
            start: before.StartUtc,
            exceptions: new CalendarEventException
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                OriginalOccurrenceStartUtc = originalStart,
                IsCancelled = true,
                CreatedByUserId = Guid.NewGuid(),
                CreatedAt = originalStart,
                UpdatedAt = originalStart,
            });
        _repo.GetEventByIdAsync(eventId, Arg.Any<CancellationToken>())
            .Returns(after);

        var sut = CreateSut();
        await sut.WarmAllAsync();

        await sut.CancelOccurrenceAsync(eventId, originalStart, Guid.NewGuid());

        // The decorator did re-fetch (parent eviction + repopulation).
        await _repo.Received(1).GetEventByIdAsync(eventId, Arg.Any<CancellationToken>());
        // The inner service was actually called.
        await _inner.Received(1).CancelOccurrenceAsync(
            eventId, originalStart, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        // The cached event now has the exception attached.
        sut.TryGet(eventId, out var refreshed).Should().BeTrue();
        refreshed.Exceptions.Should().HaveCount(1);
        refreshed.Exceptions[0].IsCancelled.Should().BeTrue();
    }

    [HumansFact]
    public async Task OverrideOccurrenceAsync_EvictsParentEventEntry_NotJustException()
    {
        // Same invariant as CancelOccurrenceAsync — override writes also flow
        // through parent-event eviction + refresh.
        var eventId = Guid.NewGuid();
        var originalStart = Instant.FromUtc(2026, 9, 1, 10, 0);

        var before = BuildEvent(id: eventId, title: "Series");
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent> { before });

        var after = BuildEvent(
            id: eventId, title: "Series",
            exceptions: new CalendarEventException
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                OriginalOccurrenceStartUtc = originalStart,
                OverrideTitle = "Special edition",
                CreatedByUserId = Guid.NewGuid(),
                CreatedAt = originalStart,
                UpdatedAt = originalStart,
            });
        _repo.GetEventByIdAsync(eventId, Arg.Any<CancellationToken>())
            .Returns(after);

        var dto = new OverrideOccurrenceDto(
            OverrideStartUtc: null, OverrideEndUtc: null,
            OverrideTitle: "Special edition",
            OverrideDescription: null,
            OverrideLocation: null,
            OverrideLocationUrl: null);

        var sut = CreateSut();
        await sut.WarmAllAsync();

        await sut.OverrideOccurrenceAsync(eventId, originalStart, dto, Guid.NewGuid());

        await _repo.Received(1).GetEventByIdAsync(eventId, Arg.Any<CancellationToken>());
        sut.TryGet(eventId, out var refreshed).Should().BeTrue();
        refreshed.Exceptions.Should().HaveCount(1);
        refreshed.Exceptions[0].OverrideTitle.Should().Be("Special edition");
    }

    [HumansFact]
    public async Task WarmAllAsync_IsIdempotent()
    {
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEvent>());

        var sut = CreateSut();

        await sut.WarmAllAsync();
        await sut.WarmAllAsync();
        await sut.WarmAllAsync();

        // Second + third calls short-circuit on _isLoaded.
        await _repo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }
}
