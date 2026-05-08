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
public sealed class VolunteerTrackingRepository : IVolunteerTrackingRepository
{
    private readonly HumansDbContext _db;

    public VolunteerTrackingRepository(HumansDbContext db) => _db = db;

    public Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default) =>
        _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

    public async Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        await _db.VolunteerBuildStatuses
            .Where(x => x.EventSettingsId == eventSettingsId)
            .ToListAsync(ct);

    public async Task<VolunteerBuildStatus> UpsertCampSetupAsync(
        Guid userId, Guid eventSettingsId, LocalDate? barrioSetupStartDate,
        string? notes, Guid? setByUserId, Instant? setAt, CancellationToken ct = default)
    {
        var existing = await _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

        if (existing is null)
        {
            var row = new VolunteerBuildStatus
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EventSettingsId = eventSettingsId,
                BarrioSetupStartDate = barrioSetupStartDate,
                Notes = notes,
                SetByUserId = setByUserId,
                SetAt = setAt,
                BlockedDayOffsets = new(),
            };
            _db.VolunteerBuildStatuses.Add(row);
            await _db.SaveChangesAsync(ct);
            return row;
        }

        existing.BarrioSetupStartDate = barrioSetupStartDate;
        existing.Notes = notes;
        existing.SetByUserId = setByUserId;
        existing.SetAt = setAt;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<IReadOnlyList<int>> ReplaceBlockedDaysAsync(
        Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets,
        CancellationToken ct = default)
    {
        var existing = await _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

        // arch:db-sort-ok normalization for canonical jsonb storage (sorted+deduped), not a display sort
        var normalized = dayOffsets.Distinct().OrderBy(x => x).ToList();

        if (existing is null)
        {
            var row = new VolunteerBuildStatus
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EventSettingsId = eventSettingsId,
                BlockedDayOffsets = normalized,
            };
            _db.VolunteerBuildStatuses.Add(row);
            await _db.SaveChangesAsync(ct);
            return Array.Empty<int>();
        }

        var prior = existing.BlockedDayOffsets.ToList();
        existing.BlockedDayOffsets = normalized;
        await _db.SaveChangesAsync(ct);
        return prior;
    }

    public async Task<bool> SetBlockAsync(
        Guid userId, Guid eventSettingsId, int dayOffset, bool block,
        CancellationToken ct = default)
    {
        var existing = await _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

        if (existing is null)
        {
            if (!block) return false;
            _db.VolunteerBuildStatuses.Add(new VolunteerBuildStatus
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EventSettingsId = eventSettingsId,
                BlockedDayOffsets = new() { dayOffset },
            });
            await _db.SaveChangesAsync(ct);
            return true;
        }

        var contained = existing.BlockedDayOffsets.Contains(dayOffset);
        if (block == contained) return false;

        if (block)
        {
            existing.BlockedDayOffsets.Add(dayOffset);
            existing.BlockedDayOffsets.Sort();
        }
        else
        {
            existing.BlockedDayOffsets.Remove(dayOffset);
        }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        var es = await _db.EventSettings
            .Where(x => x.Id == eventSettingsId)
            .Select(x => new { x.BuildStartOffset })
            .FirstOrDefaultAsync(ct);

        if (es is null) return Array.Empty<EligibleBuildSignup>();

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
