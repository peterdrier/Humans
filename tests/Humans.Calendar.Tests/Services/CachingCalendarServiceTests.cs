using AwesomeAssertions;
using Humans.Calendar.Contracts;
using Humans.Calendar.Models;
using Humans.Calendar.Services.Dtos;
using Humans.Calendar.Services;
using Humans.Teams.Contracts;
using Humans.Calendar.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Calendar.Tests.Services;

public sealed class CachingCalendarServiceTests
{
    private readonly ICalendarService _inner = Substitute.For<ICalendarService>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly ILogger<CachingCalendarService> _logger = Substitute.For<ILogger<CachingCalendarService>>();

    private CachingCalendarService CreateSut(params ICalendarFeedContributor[] contributors)
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<ICalendarService>(
            CachingCalendarService.InnerServiceKey, (_, _) => _inner);
        services.AddScoped(_ => _teamService);
        foreach (var contributor in contributors)
            services.AddScoped(_ => contributor);

        return new CachingCalendarService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _logger);
    }

    private static Task WarmAsync(CachingCalendarService sut) =>
        ((IHostedService)sut).StartAsync(Xunit.TestContext.Current.CancellationToken);

    [HumansFact]
    public async Task DateOccurrenceCancellation_RefreshesDatesAndForwardsDateIdentity()
    {
        var day = new LocalDate(2026, 3, 29);
        var before = BuildInfo() with
        {
            IsAllDay = true,
            StartUtc = null,
            EndUtc = null,
            StartDate = day,
            EndDateExclusive = day.PlusDays(1),
            RecurrenceRule = "FREQ=DAILY;COUNT=1",
            RecurrenceTimezone = null,
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
    public async Task GetOccurrencesInWindowAsync_MergesCommunityContributorItemsWithOwnEvents()
    {
        var ownEvent = BuildInfo(title: "Own event", start: Instant.FromUtc(2026, 6, 5, 9, 0), end: Instant.FromUtc(2026, 6, 5, 10, 0));
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([ownEvent]);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var item = MakeItem("Workgroups", Instant.FromUtc(2026, 6, 5, 14, 0));
        var sut = CreateSut(new FakeContributor(item));

        var results = await sut.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0), Instant.FromUtc(2026, 6, 30, 0, 0),
            ct: Xunit.TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        var contributed = results.Single(r => r.IsCommunityContribution());
        contributed.Title.Should().Be(item.Summary);
        contributed.Source.Should().Be("Workgroups");
        contributed.Url.Should().Be(item.Url);
        results.Should().Contain(r => r.EventId == ownEvent.Id && !r.IsCommunityContribution());
    }

    [HumansFact]
    public async Task GetOccurrencesInWindowAsync_OrdersContributedItemsBeforeLaterAllDayEvents()
    {
        var allDay = BuildInfo(title: "All-day", start: null, end: null) with
        {
            IsAllDay = true,
            StartUtc = null,
            EndUtc = null,
            StartDate = new LocalDate(2026, 6, 6),
            EndDateExclusive = new LocalDate(2026, 6, 7),
        };
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([allDay]);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var item = MakeItem("Workgroups", Instant.FromUtc(2026, 6, 5, 14, 0));
        var sut = CreateSut(new FakeContributor(item));

        var results = await sut.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0), Instant.FromUtc(2026, 6, 30, 0, 0),
            ct: Xunit.TestContext.Current.CancellationToken);

        results.Select(r => r.Title).Should().Equal(item.Summary, "All-day");
    }

    [HumansFact]
    public async Task GetOccurrencesInWindowAsync_PassesRequestedWindowToContributor()
    {
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([]);
        var contributor = Substitute.For<ICalendarFeedContributor>();
        contributor.GetPublicItemsForWindowAsync(Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CalendarFeedItem>)[]);
        var sut = CreateSut(contributor);

        var from = Instant.FromUtc(2026, 6, 1, 0, 0);
        var to = Instant.FromUtc(2026, 6, 30, 0, 0);
        await sut.GetOccurrencesInWindowAsync(from, to, ct: Xunit.TestContext.Current.CancellationToken);

        await contributor.Received(1).GetPublicItemsForWindowAsync(from, to, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetOccurrencesInWindowAsync_SkipsThrowingContributorAndKeepsOwnEvents()
    {
        var ownEvent = BuildInfo(title: "Own event", start: Instant.FromUtc(2026, 6, 5, 9, 0), end: Instant.FromUtc(2026, 6, 5, 10, 0));
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([ownEvent]);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var sut = CreateSut(new FakeContributor(new InvalidOperationException("boom")));

        var results = await sut.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0), Instant.FromUtc(2026, 6, 30, 0, 0),
            ct: Xunit.TestContext.Current.CancellationToken);

        results.Should().ContainSingle();
        results[0].EventId.Should().Be(ownEvent.Id);
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("FakeContributor")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [HumansFact]
    public async Task GetOccurrencesInWindowAsync_PropagatesRequestCancellationFromContributor()
    {
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([]);
        var contributor = Substitute.For<ICalendarFeedContributor>();
        contributor.GetPublicItemsForWindowAsync(Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromCanceled<IReadOnlyList<CalendarFeedItem>>((CancellationToken)call[2]!));
        var sut = CreateSut(contributor);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => sut.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0), Instant.FromUtc(2026, 6, 30, 0, 0),
            ct: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task GetOccurrencesInWindowAsync_TeamFilterExcludesContributorItems()
    {
        var teamId = Guid.NewGuid();
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([]);
        var contributor = Substitute.For<ICalendarFeedContributor>();
        var sut = CreateSut(contributor);

        var results = await sut.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0), Instant.FromUtc(2026, 6, 30, 0, 0), teamId,
            Xunit.TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
        await contributor.DidNotReceive().GetPublicItemsForWindowAsync(
            Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    private static CalendarFeedItem MakeItem(string source, Instant start) => new(
        Uid: $"{source}-1@humans.nobodies.team",
        Source: source,
        Summary: $"{source} meeting",
        Description: null,
        Start: start,
        End: start.Plus(Duration.FromHours(1)),
        Location: null,
        Url: "/Workgroups/Mine");

    private sealed class FakeContributor : ICalendarFeedContributor
    {
        private readonly CalendarFeedItem[] _items;
        private readonly Exception? _throw;

        public FakeContributor(params CalendarFeedItem[] items) => _items = items;

        public FakeContributor(Exception throwOnCall)
        {
            _items = [];
            _throw = throwOnCall;
        }

        public Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CalendarFeedItem>>(_items);

        public Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(Instant from, Instant to, CancellationToken ct)
        {
            if (_throw is not null) throw _throw;
            return Task.FromResult<IReadOnlyList<CalendarFeedItem>>(_items);
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateEventWithResultAsync_DelegatesToInnerAndRefreshesEntry(bool abortAfterCommit)
    {
        using var cancellation = new CancellationTokenSource();
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
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return CalendarOccurrenceExpander.ToInfo(created); });

        var sut = CreateSut();
        await WarmAsync(sut);

        _inner.When(inner => inner.CreateEventWithResultAsync(dto, Arg.Any<Guid>(), cancellation.Token))
            .Do(_ => { if (abortAfterCommit) cancellation.Cancel(); });
        await sut.CreateEventWithResultAsync(dto, Guid.NewGuid(), cancellation.Token);
        sut.IsWarmedUp.Should().BeTrue();

        sut.ContainsKey(created.Id).Should().BeTrue();
        await _inner.Received(1).CreateEventWithResultAsync(dto, Arg.Any<Guid>(), cancellation.Token);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateEventWithResultAsync_RefreshesCommittedEntry(bool abortAfterCommit)
    {
        using var cancellation = new CancellationTokenSource();
        var before = BuildInfo(title: "Before edit");
        var after = before with { Title = "After edit" };
        var updated = new CalendarEvent { Id = before.Id, Title = after.Title };
        var dto = new CreateCalendarEventDto(after.Title, null, null, null, before.OwningTeamId,
            before.StartUtc, before.EndUtc, false, null, null);
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([before]);
        _inner.GetEventInfoAsync(before.Id, Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return after; });
        _inner.UpdateEventWithResultAsync(before.Id, dto, Arg.Any<Guid>(), cancellation.Token)
            .Returns(_ =>
            {
                if (abortAfterCommit) cancellation.Cancel();
                return CalendarEventMutationResult.Success(updated);
            });
        var sut = CreateSut();
        await WarmAsync(sut);

        var result = await sut.UpdateEventWithResultAsync(before.Id, dto, Guid.NewGuid(), cancellation.Token);

        result.Succeeded.Should().BeTrue();
        sut.IsWarmedUp.Should().BeTrue();
        (await sut.GetEventInfoAsync(before.Id, TestContext.Current.CancellationToken))!.Title.Should().Be(after.Title);
        await _inner.Received(1).UpdateEventWithResultAsync(before.Id, dto, Arg.Any<Guid>(), cancellation.Token);
    }

    // Invariant: a per-occurrence write has no cache row of its own, so it must evict and
    // reload the PARENT event. Without the ReplaceAsync(eventId) in the decorator, every
    // read serves the pre-cancel series until the process restarts.
    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOccurrenceAsync_RefreshesTheParentEventEntry(bool abortAfterCommit)
    {
        using var cancellation = new CancellationTokenSource();
        var before = BuildInfo(title: "Weekly standup");
        var after = before with { Title = "Weekly standup (one cancelled)" };
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([before]);
        _inner.GetEventInfoAsync(before.Id, Arg.Any<CancellationToken>()).Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return after; });

        var sut = CreateSut();
        await WarmAsync(sut);

        var occurrence = Instant.FromUtc(2026, 6, 8, 10, 0);
        _inner.When(inner => inner.CancelOccurrenceAsync(before.Id, occurrence, Arg.Any<Guid>(), cancellation.Token))
            .Do(_ => { if (abortAfterCommit) cancellation.Cancel(); });
        await sut.CancelOccurrenceAsync(before.Id, occurrence, Guid.NewGuid(), cancellation.Token);
        sut.IsWarmedUp.Should().BeTrue();

        await _inner.Received(1).CancelOccurrenceAsync(
            before.Id, occurrence, Arg.Any<Guid>(), cancellation.Token);
        var reloaded = await sut.GetEventByIdAsync(before.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded!.Title.Should().Be(after.Title, because: "the parent entry is reloaded after a per-occurrence write");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverrideOccurrenceAsync_RefreshesTheParentEventEntry(bool abortAfterCommit)
    {
        using var cancellation = new CancellationTokenSource();
        var before = BuildInfo(title: "Weekly standup");
        var after = before with { Title = "Weekly standup (one moved)" };
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>()).Returns([before]);
        _inner.GetEventInfoAsync(before.Id, Arg.Any<CancellationToken>()).Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return after; });

        var sut = CreateSut();
        await WarmAsync(sut);

        var occurrence = Instant.FromUtc(2026, 6, 8, 10, 0);
        var dto = new OverrideOccurrenceDto(
            Instant.FromUtc(2026, 6, 8, 14, 0), Instant.FromUtc(2026, 6, 8, 15, 0),
            null, null, null, null);
        _inner.When(inner => inner.OverrideOccurrenceAsync(before.Id, occurrence, dto, Arg.Any<Guid>(), cancellation.Token))
            .Do(_ => { if (abortAfterCommit) cancellation.Cancel(); });
        await sut.OverrideOccurrenceAsync(before.Id, occurrence, dto, Guid.NewGuid(), cancellation.Token);
        sut.IsWarmedUp.Should().BeTrue();

        await _inner.Received(1).OverrideOccurrenceAsync(
            before.Id, occurrence, dto, Arg.Any<Guid>(), cancellation.Token);
        var reloaded = await sut.GetEventByIdAsync(before.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded!.Title.Should().Be(after.Title, because: "the parent entry is reloaded after a per-occurrence write");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteEventAsync_TombstonesMissingEntry(bool abortAfterCommit)
    {
        using var cancellation = new CancellationTokenSource();
        var info = BuildInfo(title: "Deleted");
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>())
            .Returns([info]);
        _inner.GetEventInfoAsync(info.Id, Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return (CalendarEventInfo?)null; });

        var sut = CreateSut();
        await WarmAsync(sut);

        _inner.When(inner => inner.DeleteEventAsync(info.Id, Arg.Any<Guid>(), cancellation.Token))
            .Do(_ => { if (abortAfterCommit) cancellation.Cancel(); });
        await sut.DeleteEventAsync(info.Id, Guid.NewGuid(), cancellation.Token);
        sut.IsWarmedUp.Should().BeTrue();

        sut.ContainsKey(info.Id).Should().BeFalse();
        await _inner.Received(1).DeleteEventAsync(info.Id, Arg.Any<Guid>(), cancellation.Token);
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
