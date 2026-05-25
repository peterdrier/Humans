using AwesomeAssertions;
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Calendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Calendar;

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
        ((IHostedService)sut).StartAsync(CancellationToken.None);

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
    public async Task GetEventByIdAsync_AfterWarmup_DoesNotHitInner()
    {
        var info = BuildInfo(title: "Cached event");
        _inner.GetAllEventInfosAsync(Arg.Any<CancellationToken>())
            .Returns([info]);

        var sut = CreateSut();
        await WarmAsync(sut);

        var detail = await sut.GetEventByIdAsync(info.Id);

        detail.Should().NotBeNull();
        detail!.Id.Should().Be(info.Id);
        detail.Title.Should().Be("Cached event");
        await _inner.DidNotReceive().GetEventByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
            Instant.FromUtc(2026, 6, 30, 0, 0));

        results.Should().ContainSingle();
        results[0].EventId.Should().Be(inWindow.Id);
        await _inner.DidNotReceive().GetOccurrencesInWindowAsync(
            Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateEventAsync_DelegatesToInnerAndRefreshesEntry()
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
        _inner.CreateEventAsync(dto, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(created);
        _inner.GetEventInfoAsync(created.Id, Arg.Any<CancellationToken>())
            .Returns(Humans.Application.Services.Calendar.CalendarOccurrenceExpander.ToInfo(created));

        var sut = CreateSut();
        await WarmAsync(sut);

        var result = await sut.CreateEventAsync(dto, Guid.NewGuid());

        result.Should().BeSameAs(created);
        sut.ContainsKey(created.Id).Should().BeTrue();
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

        await sut.DeleteEventAsync(info.Id, Guid.NewGuid());

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
