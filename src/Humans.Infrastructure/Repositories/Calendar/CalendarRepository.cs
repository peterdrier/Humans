using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Calendar;

/// <summary>
/// EF-backed implementation of <see cref="ICalendarRepository"/>. The only
/// non-test file that touches the Calendar-owned DbSets
/// (<c>CalendarEvents</c>, <c>CalendarEventExceptions</c>) after the Calendar
/// §15 migration (issue #569) lands. Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// Cross-domain navigation (<c>CalendarEvent.OwningTeam</c>) is never
/// <c>Include</c>-ed; the service stitches team names via
/// <see cref="Application.Interfaces.Teams.ITeamService"/>.
/// </summary>
public sealed class CalendarRepository : ICalendarRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public CalendarRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsInWindowAsync(
        Instant from,
        Instant to,
        Guid? teamId,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions)
            .Where(e => e.StartUtc <= to
                && (e.RecurrenceUntilUtc == null || e.RecurrenceUntilUtc >= from));

        if (teamId is { } t)
        {
            query = query.Where(e => e.OwningTeamId == t);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    // ==========================================================================
    // Writes — CalendarEvent
    // ==========================================================================

    public async Task AddAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CalendarEvents.Add(ev);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateAsync(
        Guid id,
        Action<CalendarEvent> mutate,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ev = await ctx.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null)
        {
            return false;
        }

        mutate(ev);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(Guid OwningTeamId, string Title)?> SoftDeleteAsync(
        Guid id,
        Instant deletedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ev = await ctx.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null)
        {
            return null;
        }

        ev.DeletedAt = deletedAt;
        ev.UpdatedAt = deletedAt;
        await ctx.SaveChangesAsync(ct);
        return (ev.OwningTeamId, ev.Title);
    }

    // ==========================================================================
    // Writes — CalendarEventException
    // ==========================================================================

    public async Task UpsertExceptionAsync(
        Guid eventId,
        Instant originalOccurrenceStartUtc,
        Guid createdByUserId,
        Instant now,
        Action<CalendarEventException> apply,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.CalendarEventExceptions
            .FirstOrDefaultAsync(
                x => x.EventId == eventId && x.OriginalOccurrenceStartUtc == originalOccurrenceStartUtc,
                ct);

        if (existing is null)
        {
            existing = new CalendarEventException
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                OriginalOccurrenceStartUtc = originalOccurrenceStartUtc,
                CreatedByUserId = createdByUserId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ctx.CalendarEventExceptions.Add(existing);
        }
        else
        {
            existing.UpdatedAt = now;
        }

        apply(existing);

        var errors = existing.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Exception is invalid: " + string.Join("; ", errors));
        }

        await ctx.SaveChangesAsync(ct);
    }
}
