using Humans.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Calendar.Data;

/// <summary>
/// EF-backed implementation of <see cref="ICalendarRepository"/>. The only
/// non-test file that touches the Calendar-owned DbSets
/// (<c>CalendarEvents</c>, <c>CalendarEventExceptions</c>, <c>CalendarFeedTokens</c>). Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>CalendarDbContext</c> remains Scoped.
/// The owning team is a bare Guid — not in this model at all.
/// </summary>
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

    public async Task UpsertExceptionAsync(
        Guid eventId,
        Instant? originalOccurrenceStartUtc,
        Guid createdByUserId,
        Instant now,
        Action<CalendarEventException> apply,
        CancellationToken ct = default, LocalDate? originalDate = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Match on whichever column names the occurrence: OriginalOccurrenceDate for an
        // all-day series, OriginalOccurrenceStartUtc for a timed one. Each pairing with
        // EventId carries its own unique index, so missing the existing row here is a
        // duplicate-insert failure, not a second row. A caller migrating a pre-date-columns
        // all-day row passes both: the stale instant is what finds it, and the write below
        // moves it onto date identity.
        //
        // Bypass the soft-delete query filter on the existence lookup so that if the parent
        // event was soft-deleted between the caller's pre-check and this upsert, the
        // existing row is still found and updated.
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
            throw new InvalidOperationException("Exception is invalid: " + string.Join("; ", errors));
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<Guid?> GetFeedTokenAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.CalendarFeedTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == userId, ct);
        return row?.Token;
    }

    public async Task<Guid> GetOrAddFeedTokenAsync(
        Guid userId, Guid candidate, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (await ctx.CalendarFeedTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.UserId == userId, ct) is { } existing)
        {
            return existing.Token;
        }

        ctx.CalendarFeedTokens.Add(new CalendarFeedToken { UserId = userId, Token = candidate });
        try
        {
            await ctx.SaveChangesAsync(ct);
            return candidate;
        }
        catch (DbUpdateException)
        {
            // Another first-time view inserted between the read above and this write.
            // Reload and hand back the winner's token: the member gets one working URL
            // either way. No concurrency token is involved, and none is wanted
            // (memory/architecture/no-concurrency-tokens.md) — the primary key is
            // already the only guard this needs.
            ctx.ChangeTracker.Clear();
            var winner = await ctx.CalendarFeedTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.UserId == userId, ct);

            // Nothing there means the write failed for some other reason, which is
            // not ours to swallow.
            if (winner is null) throw;
            return winner.Token;
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
        // Load+remove rather than ExecuteDeleteAsync: the in-memory provider the
        // section's tests run on does not support the latter.
        var row = await ctx.CalendarFeedTokens.FirstOrDefaultAsync(t => t.UserId == userId, ct);
        if (row is null)
        {
            return;
        }

        ctx.CalendarFeedTokens.Remove(row);
        await ctx.SaveChangesAsync(ct);
    }
}
