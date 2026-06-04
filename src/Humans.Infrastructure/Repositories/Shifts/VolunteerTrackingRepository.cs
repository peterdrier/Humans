using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

/// <summary>
/// EF-backed implementation of <see cref="IVolunteerTrackingRepository"/>. This
/// user-oriented Shifts repository owns <c>DbContext.VolunteerBuildStatuses</c>
/// and <c>DbContext.GeneralAvailability</c>.
/// </summary>
/// <remarks>
/// Uses the Scoped <see cref="HumansDbContext"/> directly (same pattern as
/// <see cref="ShiftRepository"/>) so multi-step mutations on
/// <see cref="VolunteerBuildStatus"/> share one EF change-tracker.
/// </remarks>
internal sealed class VolunteerTrackingRepository(HumansDbContext db) : IVolunteerTrackingRepository
{
    public async Task<IReadOnlyList<VolunteerBuildStatus>> GetBuildStatusesForEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken ct = default)
    {
        if (userIds is { Count: 0 }) return [];

        var query = db.VolunteerBuildStatuses
            .AsNoTracking()
            .Where(x => x.EventSettingsId == eventSettingsId);

        if (userIds is not null)
            query = query.Where(x => userIds.Contains(x.UserId));

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GeneralAvailability>> GetAvailabilityForEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken ct = default)
    {
        if (userIds is { Count: 0 }) return [];

        var query = db.GeneralAvailability
            .AsNoTracking()
            .Where(g => g.EventSettingsId == eventSettingsId);

        if (userIds is not null)
            query = query.Where(g => userIds.Contains(g.UserId));

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GeneralAvailability>> GetAvailabilityForUserAsync(
        Guid userId,
        Guid? eventSettingsId = null,
        CancellationToken ct = default)
    {
        var query = db.GeneralAvailability
            .AsNoTracking()
            .Where(g => g.UserId == userId);

        if (eventSettingsId is { } eventId)
            query = query.Where(g => g.EventSettingsId == eventId);

        return await query.ToListAsync(ct);
    }

    public async Task UpsertAvailabilityAsync(
        Guid userId,
        Guid eventSettingsId,
        IReadOnlyList<int> dayOffsets,
        Instant now,
        CancellationToken ct = default)
    {
        var existing = await db.GeneralAvailability
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
            db.GeneralAvailability.Add(new GeneralAvailability
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EventSettingsId = eventSettingsId,
                AvailableDayOffsets = dayOffsets.ToList(),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<int> ReassignAvailabilityToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        var sourceRows = await db.GeneralAvailability
            .Where(g => g.UserId == sourceUserId)
            .ToListAsync(ct);

        var targetEventIds = await db.GeneralAvailability
            .Where(g => g.UserId == targetUserId)
            .Select(g => g.EventSettingsId)
            .ToListAsync(ct);
        var targetEventIdSet = new HashSet<Guid>(targetEventIds);

        foreach (var src in sourceRows)
        {
            if (targetEventIdSet.Contains(src.EventSettingsId))
            {
                db.GeneralAvailability.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
            }
        }

        await db.SaveChangesAsync(ct);

        return await db.GeneralAvailability
            .CountAsync(g => g.UserId == targetUserId, ct);
    }

    public async Task<IReadOnlyList<int>> UpsertCampSetupAsync(
        Guid userId, Guid eventSettingsId, LocalDate? barrioSetupStartDate,
        string? notes, Guid? setByUserId, Instant? setAt,
        int? setupOffsetThreshold, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(userId, eventSettingsId, ct);

        row.BarrioSetupStartDate = barrioSetupStartDate;
        row.Notes = notes;
        row.SetByUserId = setByUserId;
        row.SetAt = setAt;

        IReadOnlyList<int> trimmed = [];
        if (setupOffsetThreshold is { } threshold)
        {
            // DayOffs is persisted sorted (see UpsertDayOffAsync), so the
            // filter preserves canonical order — no resort needed.
            var toTrim = row.DayOffs
                .Where(d => d.DayOffset >= threshold)
                .Select(d => d.DayOffset)
                .ToArray();
            if (toTrim.Length > 0)
            {
                row.DayOffs.RemoveAll(d => d.DayOffset >= threshold);
                trimmed = toTrim;
            }
        }

        await db.SaveChangesAsync(ct);
        return trimmed;
    }

    public async Task UpsertDayOffAsync(
        Guid userId, Guid eventSettingsId, DayOffEntry entry,
        CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(userId, eventSettingsId, ct);

        row.DayOffs.RemoveAll(d => d.DayOffset == entry.DayOffset);
        row.DayOffs.Add(entry);
        // arch:db-sort-ok normalization for canonical jsonb storage (sorted), not a display sort
        row.DayOffs.Sort((a, b) => a.DayOffset.CompareTo(b.DayOffset));
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveDayOffAsync(
        Guid userId, Guid eventSettingsId, int dayOffset,
        CancellationToken ct = default)
    {
        var existing = await db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

        if (existing is null) return false;

        var removed = existing.DayOffs.RemoveAll(d => d.DayOffset == dayOffset) > 0;
        if (removed)
        {
            await db.SaveChangesAsync(ct);
        }
        return removed;
    }

    /// <summary>
    /// Loads the row for (userId, eventSettingsId) or creates a tracked-but-
    /// not-saved one with empty DayOffs. Either way the caller mutates the
    /// returned row and SaveChangesAsync once.
    /// </summary>
    private async Task<VolunteerBuildStatus> GetOrCreateAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct)
    {
        var existing = await db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);
        if (existing is not null) return existing;

        var row = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = eventSettingsId,
        };
        db.VolunteerBuildStatuses.Add(row);
        return row;
    }

}
