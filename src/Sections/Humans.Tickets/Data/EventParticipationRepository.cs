using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Tickets.Data;

/// <summary>EF-backed <see cref="IEventParticipationRepository"/>; singleton over a context factory.</summary>
internal sealed class EventParticipationRepository(IDbContextFactory<TicketsDbContext> factory)
    : IEventParticipationRepository
{
    public async Task<IReadOnlyList<EventParticipation>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventParticipations.AsNoTracking().Where(ep => ep.UserId == userId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EventParticipation>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventParticipations.AsNoTracking().ToListAsync(ct);
    }

    public async Task<bool> UpsertAsync(
        Guid userId, int year, ParticipationStatus status, ParticipationSource source,
        Instant? declaredAt, Instant? checkedInAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);
        if (existing is null)
        {
            ctx.EventParticipations.Add(new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Year = year,
                Status = status,
                Source = source,
                DeclaredAt = declaredAt,
                CheckedInAt = checkedInAt,
            });
        }
        else
        {
            // Preserve attended metadata; a later real scan may fill a missing arrival.
            if (existing.Status == ParticipationStatus.Attended)
            {
                if (status != ParticipationStatus.Attended || existing.CheckedInAt is not null || checkedInAt is null)
                    return false;
            }
            else
            {
                existing.Status = status;
                existing.Source = source;
                existing.DeclaredAt = declaredAt;
            }
            // Arrival is write-once, including when attendance was known before its timestamp.
            existing.CheckedInAt ??= checkedInAt;
        }

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ClearAsync(
        Guid userId, int year, ParticipationSource requiredSource, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

        if (existing?.Status is null
            || existing.Source != requiredSource
            || existing.Status == ParticipationStatus.Attended)
        {
            return false;
        }

        ctx.EventParticipations.Remove(existing);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<(Guid UserId, ParticipationStatus Status)>> BackfillAsync(
        int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventParticipations
            .Where(ep => ep.Year == year)
            .ToDictionaryAsync(ep => ep.UserId, ct);

        // Compare each touched record's final state with where it started, so a user listed twice
        // reports once with the status that was committed.
        var before = new Dictionary<Guid, (ParticipationStatus? Status, ParticipationSource Source, Instant? DeclaredAt)>();
        foreach (var (userId, status) in entries)
        {
            if (!existing.TryGetValue(userId, out var ep))
            {
                ep = new EventParticipation { Id = Guid.NewGuid(), UserId = userId, Year = year };
                ctx.EventParticipations.Add(ep);
                existing[userId] = ep;
            }
            before.TryAdd(userId, (ep.Status, ep.Source, ep.DeclaredAt));

            // Attended is permanent — leave it alone.
            if (ep.Status == ParticipationStatus.Attended)
                continue;

            ep.Status = status;
            ep.Source = ParticipationSource.AdminBackfill;
            ep.DeclaredAt = null;
        }

        var changed = before
            .Where(b => (existing[b.Key].Status, existing[b.Key].Source, existing[b.Key].DeclaredAt) != b.Value)
            .Select(b => (b.Key, existing[b.Key].Status!.Value))
            .ToList();
        await ctx.SaveChangesAsync(ct);
        return changed;
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.EventParticipations
            .Where(ep => ep.UserId == sourceUserId || ep.UserId == targetUserId)
            .ToListAsync(ct);

        var sourceByYear = rows.Where(ep => ep.UserId == sourceUserId).ToDictionary(ep => ep.Year);
        var targetByYear = rows.Where(ep => ep.UserId == targetUserId).ToDictionary(ep => ep.Year);

        foreach (var (year, sourceRow) in sourceByYear)
        {
            ctx.EventParticipations.Remove(sourceRow);
            if (sourceRow.Status is null)
                continue;

            if (!targetByYear.TryGetValue(year, out var targetRow))
            {
                targetRow = new EventParticipation { Id = Guid.NewGuid(), UserId = targetUserId, Year = year };
                ctx.EventParticipations.Add(targetRow);
            }

            if (StatusPrecedence(sourceRow.Status) > StatusPrecedence(targetRow.Status))
            {
                targetRow.Status = sourceRow.Status;
                targetRow.Source = sourceRow.Source;
                targetRow.DeclaredAt = sourceRow.DeclaredAt;
            }
            targetRow.CheckedInAt ??= sourceRow.CheckedInAt;
        }

        await ctx.SaveChangesAsync(ct);
    }

    private static int StatusPrecedence(ParticipationStatus? status) => status switch
    {
        ParticipationStatus.Attended => 4,
        ParticipationStatus.Ticketed => 3,
        ParticipationStatus.NoShow => 2,
        ParticipationStatus.NotAttending => 1,
        _ => 0
    };
}
