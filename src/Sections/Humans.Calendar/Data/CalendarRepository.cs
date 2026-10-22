using Humans.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Calendar.Data;

internal sealed class CalendarRepository(IDbContextFactory<CalendarDbContext> factory) : ICalendarRepository
{
    public async Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions)
            .ToListAsync(ct);
    }

    public async Task AddAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.CalendarEvents.Add(ev);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateAsync(
        Guid id,
        Action<CalendarEvent> mutate,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ev = await ctx.CalendarEvents.Include(e => e.Exceptions).FirstOrDefaultAsync(e => e.Id == id, ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
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

    public async Task<string?> UpsertExceptionAsync(
        Guid eventId,
        Instant? originalOccurrenceStartUtc,
        Guid createdByUserId,
        Instant now,
        Action<CalendarEventException> apply,
        CancellationToken ct = default, LocalDate? originalDate = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Each identity column has its own unique index, so missing the existing row here means a duplicate-insert
        // failure. A pre-date-columns all-day row is found by its stale instant and moved to date identity below.
        // Ignore the soft-delete filter: the parent may have been deleted since the caller's pre-check.
        var existing = await ctx.CalendarEventExceptions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.EventId == eventId && ((originalDate != null && x.OriginalOccurrenceDate == originalDate) ||
                    (originalOccurrenceStartUtc != null && x.OriginalOccurrenceStartUtc == originalOccurrenceStartUtc)),
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

        existing.OriginalOccurrenceDate = originalDate;
        existing.OriginalOccurrenceStartUtc = originalDate is null ? originalOccurrenceStartUtc : null;
        apply(existing);

        var errors = existing.Validate();
        if (errors.Count > 0)
        {
            return "Exception is invalid: " + string.Join("; ", errors);
        }

        await ctx.SaveChangesAsync(ct);
        return null;
    }

    public async Task<Guid?> GetFeedTokenAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.CalendarFeedTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == userId, ct);
        return row?.Token;
    }

    public async Task<(Guid Token, bool Created)> GetOrAddFeedTokenAsync(
        Guid userId, Guid candidate, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (await ctx.CalendarFeedTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.UserId == userId, ct) is { } existing)
        {
            return (existing.Token, false);
        }

        ctx.CalendarFeedTokens.Add(new CalendarFeedToken { UserId = userId, Token = candidate });
        try
        {
            await ctx.SaveChangesAsync(ct);
            return (candidate, true);
        }
        catch (DbUpdateException)
        {
            // A racing create inserted first. Hand back the winner's token: the primary key is the only guard needed.
            ctx.ChangeTracker.Clear();
            var winner = await ctx.CalendarFeedTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.UserId == userId, ct);

            // Nothing there: the write failed for another reason, not ours to swallow.
            if (winner is null) throw;
            return (winner.Token, false);
        }
    }

    public async Task SetFeedTokenAsync(Guid userId, Guid token, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.CalendarFeedTokens.FirstOrDefaultAsync(t => t.UserId == userId, ct);
        if (row is null)
        {
            ctx.CalendarFeedTokens.Add(new CalendarFeedToken { UserId = userId, Token = token });
        }
        else
        {
            row.Token = token;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteFeedTokenAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Load+remove rather than ExecuteDeleteAsync: the in-memory provider the tests run on lacks it.
        var row = await ctx.CalendarFeedTokens.FirstOrDefaultAsync(t => t.UserId == userId, ct);
        if (row is null)
        {
            return;
        }

        ctx.CalendarFeedTokens.Remove(row);
        await ctx.SaveChangesAsync(ct);
    }
}
