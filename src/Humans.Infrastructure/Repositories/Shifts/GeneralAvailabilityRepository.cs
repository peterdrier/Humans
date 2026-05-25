using Humans.Application.Architecture;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

/// <summary>
/// EF-backed implementation of <see cref="IGeneralAvailabilityRepository"/>.
/// The only non-test file that touches <c>DbContext.GeneralAvailability</c>
/// from the <c>GeneralAvailabilityService</c> migration onward. Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
[Grandfathered("HUM0025", justification: "Shifts-section table also read by ShiftSignupRepository for conflict checks; route reads through the owning repository.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "GeneralAvailability")]
internal sealed class GeneralAvailabilityRepository(IDbContextFactory<HumansDbContext> factory)
    : IGeneralAvailabilityRepository
{
    public async Task<GeneralAvailability?> GetByUserAndEventAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GeneralAvailability
            .AsNoTracking()
            .FirstOrDefaultAsync(
                g => g.UserId == userId && g.EventSettingsId == eventSettingsId,
                ct);
    }

    public async Task<IReadOnlyList<GeneralAvailability>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GeneralAvailability
            .AsNoTracking()
            .Where(g => g.EventSettingsId == eventSettingsId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GeneralAvailability>> GetByUsersAndEventAsync(
        IReadOnlyCollection<Guid> userIds, Guid eventSettingsId, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GeneralAvailability
            .AsNoTracking()
            .Where(g => g.EventSettingsId == eventSettingsId && userIds.Contains(g.UserId))
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(
        Guid userId,
        Guid eventSettingsId,
        IReadOnlyList<int> dayOffsets,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var existing = await ctx.GeneralAvailability
            .FirstOrDefaultAsync(
                g => g.UserId == userId && g.EventSettingsId == eventSettingsId,
                ct);

        if (existing is not null)
        {
            existing.AvailableDayOffsets = dayOffsets.ToList();
            existing.UpdatedAt = now;
        }
        else
        {
            ctx.GeneralAvailability.Add(new GeneralAvailability
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EventSettingsId = eventSettingsId,
                AvailableDayOffsets = dayOffsets.ToList(),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var existing = await ctx.GeneralAvailability
            .FirstOrDefaultAsync(
                g => g.UserId == userId && g.EventSettingsId == eventSettingsId,
                ct);

        if (existing is null) return;

        ctx.GeneralAvailability.Remove(existing);
        await ctx.SaveChangesAsync(ct);
    }

    // ============================================================
    // Account-merge fold
    // ============================================================

    public async Task<int> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var sourceRows = await ctx.GeneralAvailability
            .Where(g => g.UserId == sourceUserId)
            .ToListAsync(ct);

        var targetEventIds = await ctx.GeneralAvailability
            .Where(g => g.UserId == targetUserId)
            .Select(g => g.EventSettingsId)
            .ToListAsync(ct);
        var targetEventIdSet = new HashSet<Guid>(targetEventIds);

        foreach (var src in sourceRows)
        {
            if (targetEventIdSet.Contains(src.EventSettingsId))
            {
                // Target already has availability for this event — target wins.
                ctx.GeneralAvailability.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
            }
        }

        await ctx.SaveChangesAsync(ct);

        return await ctx.GeneralAvailability
            .CountAsync(g => g.UserId == targetUserId, ct);
    }
}
