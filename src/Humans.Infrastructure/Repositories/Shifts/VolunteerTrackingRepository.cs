using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

/// <summary>
/// EF-backed implementation of <see cref="IVolunteerTrackingRepository"/>. The
/// only non-test file that touches <c>DbContext.VolunteerBuildStatuses</c> from
/// the volunteer-tracking migration onward.
/// </summary>
/// <remarks>
/// Uses the Scoped <see cref="HumansDbContext"/> directly (same pattern as
/// <see cref="ShiftSignupRepository"/>) so multi-step mutations on
/// <see cref="VolunteerBuildStatus"/> share one EF change-tracker.
/// </remarks>
internal sealed class VolunteerTrackingRepository : IVolunteerTrackingRepository
{
    private readonly HumansDbContext _db;

    public VolunteerTrackingRepository(HumansDbContext db) => _db = db;

    public Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default) =>
        _db.VolunteerBuildStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

    public async Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        await _db.VolunteerBuildStatuses
            .Where(x => x.EventSettingsId == eventSettingsId)
            .ToListAsync(ct);

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

        await _db.SaveChangesAsync(ct);
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
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveDayOffAsync(
        Guid userId, Guid eventSettingsId, int dayOffset,
        CancellationToken ct = default)
    {
        var existing = await _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

        if (existing is null) return false;

        var removed = existing.DayOffs.RemoveAll(d => d.DayOffset == dayOffset) > 0;
        if (removed)
        {
            await _db.SaveChangesAsync(ct);
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
        var existing = await _db.VolunteerBuildStatuses
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
        _db.VolunteerBuildStatuses.Add(row);
        return row;
    }

    public async Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        var es = await _db.EventSettings
            .Where(x => x.Id == eventSettingsId)
            .Select(x => new { x.BuildStartOffset })
            .FirstOrDefaultAsync(ct);

        if (es is null) return [];

        // SignupStatus and RotaPeriod are stored as strings via
        // HasConversion<string>(). Per memory/code/no-enum-compare-in-ef.md, we
        // avoid `>=`/`<=` on those enums and use an explicit `||` chain so the
        // SQL stays a literal-IN match (no lexicographic comparison).
        // DayOffset is an int — direct numeric comparison is safe.
        return await _db.ShiftSignups
            .Where(s => s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending)
            .Where(s => s.Shift.DayOffset >= es.BuildStartOffset && s.Shift.DayOffset < 0)
            .Where(s => s.Shift.Rota.Period == RotaPeriod.Build || s.Shift.Rota.Period == RotaPeriod.All)
            .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId)
            .Select(s => new EligibleBuildSignup(
                s.UserId, s.Shift.DayOffset, s.Status, s.Shift.Rota.Name))
            .ToListAsync(ct);
    }
}
