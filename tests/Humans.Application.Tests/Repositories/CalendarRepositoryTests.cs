using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Calendar;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Repository tests for <see cref="CalendarRepository"/> — verify the EF-backed
/// implementation reads/writes <c>calendar_events</c> and
/// <c>calendar_event_exceptions</c> correctly through
/// <see cref="IDbContextFactory{HumansDbContext}"/>, with no cross-domain
/// <c>.Include()</c>.
/// </summary>
public sealed class CalendarRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly CalendarRepository _repo;

    public CalendarRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new CalendarRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // AddAsync / GetEventByIdAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddAsync_PersistsEvent()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        var persisted = await _dbContext.CalendarEvents.AsNoTracking().SingleAsync();
        persisted.Id.Should().Be(ev.Id);
        persisted.Title.Should().Be(ev.Title);
    }

    [HumansFact]
    public async Task GetEventByIdAsync_ReturnsEventWithExceptions()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        await _repo.UpsertExceptionAsync(
            ev.Id,
            ev.StartUtc,
            createdByUserId: Guid.NewGuid(),
            now: Instant.FromUtc(2026, 4, 10, 0, 0),
            apply: x => x.IsCancelled = true);

        var fetched = await _repo.GetEventByIdAsync(ev.Id);
        fetched.Should().NotBeNull();
        fetched!.Exceptions.Should().ContainSingle(x => x.IsCancelled);
    }

    [HumansFact]
    public async Task GetEventByIdAsync_ReturnsNullForMissingId()
    {
        var fetched = await _repo.GetEventByIdAsync(Guid.NewGuid());
        fetched.Should().BeNull();
    }

    [HumansFact]
    public async Task GetEventByIdAsync_DoesNotLoadOwningTeamNav()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        var fetched = await _repo.GetEventByIdAsync(ev.Id);
        fetched.Should().NotBeNull();
#pragma warning disable CS0618 // accessing the [Obsolete] nav intentionally in the assertion
        fetched!.OwningTeam.Should().BeNull(
            because: "CalendarRepository must not .Include(OwningTeam) — cross-domain nav resolved via ITeamService (design-rules §6c)");
#pragma warning restore CS0618
    }

    [HumansFact]
    public async Task GetEventByIdAsync_HidesSoftDeleted()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        await _repo.SoftDeleteAsync(ev.Id, Instant.FromUtc(2026, 4, 10, 0, 0));

        var fetched = await _repo.GetEventByIdAsync(ev.Id);
        fetched.Should().BeNull();
    }

    // ==========================================================================
    // GetEventsInWindowAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetEventsInWindowAsync_FiltersByOverlap()
    {
        var inside = BuildEvent(
            start: Instant.FromUtc(2026, 6, 15, 17, 0),
            end: Instant.FromUtc(2026, 6, 15, 18, 0));
        var outside = BuildEvent(
            start: Instant.FromUtc(2027, 1, 1, 0, 0),
            end: Instant.FromUtc(2027, 1, 1, 1, 0));

        await _repo.AddAsync(inside);
        await _repo.AddAsync(outside);

        var events = await _repo.GetEventsInWindowAsync(
            from: Instant.FromUtc(2026, 6, 1, 0, 0),
            to: Instant.FromUtc(2026, 7, 1, 0, 0),
            teamId: null);

        events.Should().ContainSingle(e => e.Id == inside.Id);
        events.Should().NotContain(e => e.Id == outside.Id);
    }

    [HumansFact]
    public async Task GetEventsInWindowAsync_FiltersByTeam()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();

        var a = BuildEvent(teamId: teamA);
        var b = BuildEvent(teamId: teamB);

        await _repo.AddAsync(a);
        await _repo.AddAsync(b);

        var events = await _repo.GetEventsInWindowAsync(
            from: Instant.FromUtc(2026, 1, 1, 0, 0),
            to: Instant.FromUtc(2027, 1, 1, 0, 0),
            teamId: teamA);

        events.Should().ContainSingle(e => e.Id == a.Id);
        events.Should().NotContain(e => e.Id == b.Id);
    }

    [HumansFact]
    public async Task GetEventsInWindowAsync_IncludesExceptions()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        await _repo.UpsertExceptionAsync(
            ev.Id,
            ev.StartUtc,
            createdByUserId: Guid.NewGuid(),
            now: Instant.FromUtc(2026, 4, 10, 0, 0),
            apply: x => x.IsCancelled = true);

        var events = await _repo.GetEventsInWindowAsync(
            from: Instant.FromUtc(2026, 1, 1, 0, 0),
            to: Instant.FromUtc(2027, 1, 1, 0, 0),
            teamId: null);

        events.Should().ContainSingle();
        events[0].Exceptions.Should().ContainSingle(x => x.IsCancelled);
    }

    // ==========================================================================
    // UpdateAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateAsync_MutatesTrackedEntity()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        var updated = await _repo.UpdateAsync(ev.Id, e =>
        {
            e.Title = "Updated title";
            e.UpdatedAt = Instant.FromUtc(2026, 4, 10, 0, 0);
        });

        updated.Should().BeTrue();
        var persisted = await _repo.GetEventByIdAsync(ev.Id);
        persisted.Should().NotBeNull();
        persisted!.Title.Should().Be("Updated title");
    }

    [HumansFact]
    public async Task UpdateAsync_ReturnsFalseForMissingId()
    {
        var updated = await _repo.UpdateAsync(Guid.NewGuid(), _ => { });
        updated.Should().BeFalse();
    }

    // ==========================================================================
    // SoftDeleteAsync
    // ==========================================================================

    [HumansFact]
    public async Task SoftDeleteAsync_ReturnsTeamIdAndTitleAndStampsDeletedAt()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        var now = Instant.FromUtc(2026, 4, 10, 0, 0);
        var result = await _repo.SoftDeleteAsync(ev.Id, now);

        result.Should().NotBeNull();
        result!.Value.OwningTeamId.Should().Be(ev.OwningTeamId);
        result.Value.Title.Should().Be(ev.Title);

        var deletedRow = await _dbContext.CalendarEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(e => e.Id == ev.Id);
        deletedRow.DeletedAt.Should().Be(now);
    }

    [HumansFact]
    public async Task SoftDeleteAsync_ReturnsNullForMissingId()
    {
        var result = await _repo.SoftDeleteAsync(Guid.NewGuid(), Instant.FromUtc(2026, 4, 10, 0, 0));
        result.Should().BeNull();
    }

    // ==========================================================================
    // UpsertExceptionAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpsertExceptionAsync_InsertsNewRowWhenMissing()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        await _repo.UpsertExceptionAsync(
            ev.Id,
            ev.StartUtc,
            createdByUserId: Guid.NewGuid(),
            now: Instant.FromUtc(2026, 4, 10, 0, 0),
            apply: x => x.IsCancelled = true);

        var exceptions = await _dbContext.CalendarEventExceptions.AsNoTracking().ToListAsync();
        exceptions.Should().ContainSingle();
        exceptions[0].IsCancelled.Should().BeTrue();
    }

    [HumansFact]
    public async Task UpsertExceptionAsync_UpdatesExistingRowWhenPresent()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        await _repo.UpsertExceptionAsync(
            ev.Id,
            ev.StartUtc,
            createdByUserId: Guid.NewGuid(),
            now: Instant.FromUtc(2026, 4, 10, 0, 0),
            apply: x => x.IsCancelled = true);

        await _repo.UpsertExceptionAsync(
            ev.Id,
            ev.StartUtc,
            createdByUserId: Guid.NewGuid(),
            now: Instant.FromUtc(2026, 4, 11, 0, 0),
            apply: x =>
            {
                x.IsCancelled = false;
                x.OverrideTitle = "Overridden";
            });

        var exceptions = await _dbContext.CalendarEventExceptions.AsNoTracking().ToListAsync();
        exceptions.Should().ContainSingle(x =>
            !x.IsCancelled && x.OverrideTitle == "Overridden");
    }

    [HumansFact]
    public async Task UpsertExceptionAsync_ThrowsWhenNeitherCancelledNorOverridden()
    {
        var ev = BuildEvent();
        await _repo.AddAsync(ev);

        var act = () => _repo.UpsertExceptionAsync(
            ev.Id,
            ev.StartUtc,
            createdByUserId: Guid.NewGuid(),
            now: Instant.FromUtc(2026, 4, 10, 0, 0),
            apply: _ => { });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static CalendarEvent BuildEvent(
        Guid? teamId = null,
        Instant? start = null,
        Instant? end = null) => new()
        {
            Id = Guid.NewGuid(),
            Title = "Test event",
            OwningTeamId = teamId ?? Guid.NewGuid(),
            StartUtc = start ?? Instant.FromUtc(2026, 6, 15, 17, 0),
            EndUtc = end ?? Instant.FromUtc(2026, 6, 15, 18, 0),
            IsAllDay = false,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Instant.FromUtc(2026, 4, 1, 12, 0),
            UpdatedAt = Instant.FromUtc(2026, 4, 1, 12, 0),
        };
}
