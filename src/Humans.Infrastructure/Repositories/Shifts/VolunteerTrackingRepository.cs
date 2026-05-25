using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Architecture;
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
[Grandfathered("HUM0025", justification: "Shifts-section table shared across the Shifts repositories; converge them on one owner per table.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "EventSettings")]
[Grandfathered("HUM0025", justification: "Shifts-section table shared across the Shifts repositories; converge them on one owner per table.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "ShiftSignups")]
internal sealed class VolunteerTrackingRepository(HumansDbContext db) : IVolunteerTrackingRepository
{
    public Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default) =>
        db.VolunteerBuildStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

    public async Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        await db.VolunteerBuildStatuses
            .Where(x => x.EventSettingsId == eventSettingsId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VolunteerBuildStatus>> GetByUsersAndEventAsync(
        IReadOnlyCollection<Guid> userIds, Guid eventSettingsId, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        return await db.VolunteerBuildStatuses
            .AsNoTracking()
            .Where(x => x.EventSettingsId == eventSettingsId && userIds.Contains(x.UserId))
            .ToListAsync(ct);
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

    public async Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        var es = await db.EventSettings
            .Where(x => x.Id == eventSettingsId)
            .Select(x => new { x.BuildStartOffset })
            .FirstOrDefaultAsync(ct);

        if (es is null) return [];

        // SignupStatus and RotaPeriod are stored as strings via
        // HasConversion<string>(). Per memory/code/no-enum-compare-in-ef.md, we
        // avoid `>=`/`<=` on those enums and use an explicit `||` chain so the
        // SQL stays a literal-IN match (no lexicographic comparison).
        // DayOffset is an int — direct numeric comparison is safe.
        return await db.ShiftSignups
            .Where(s => s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending)
            .Where(s => s.Shift.DayOffset >= es.BuildStartOffset && s.Shift.DayOffset < 0)
            .Where(s => s.Shift.Rota.Period == RotaPeriod.Build || s.Shift.Rota.Period == RotaPeriod.All)
            .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId)
            .Select(s => new EligibleBuildSignup(
                s.UserId, s.Shift.DayOffset, s.Status, s.Shift.Rota.Name))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
        Guid eventSettingsId,
        LocalDate startDate,
        LocalDate endDate,
        Guid? departmentId,
        CancellationToken ct)
    {
        // Look up the event so we can resolve absolute shift times in memory.
        // Shift.StartsAtUtc / EndsAtUtc are NOT stored columns: absolute times are
        // computed from (GateOpeningDate + DayOffset + StartTime + Duration) via
        // Shift.GetAbsoluteStart / GetAbsoluteEnd, which involve a NodaTime zone
        // conversion that cannot be translated to SQL. We narrow in SQL by
        // DayOffset and finalise the overlap check in memory.
        var settings = await db.EventSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventSettingsId, ct)
            ?? throw new InvalidOperationException($"EventSettings {eventSettingsId} not found.");

        var zone = DateTimeZoneProviders.Tzdb[settings.TimeZoneId];
        var rangeStartUtc = startDate.AtStartOfDayInZone(zone).ToInstant();
        var rangeEndUtcExclusive = endDate.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();

        // DayOffset window matching [startDate, endDate] inclusive, with a one-day
        // buffer on either side to defend against per-shift wall-time edge cases
        // and DST. Final filter below clips to the real instant overlap.
        var startOffset = Period.Between(settings.GateOpeningDate, startDate, PeriodUnits.Days).Days - 1;
        var endOffset = Period.Between(settings.GateOpeningDate, endDate, PeriodUnits.Days).Days + 1;

        var query = db.ShiftSignups
            .AsNoTracking()
            .Where(s => s.Status == SignupStatus.Confirmed)
            .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId)
            .Where(s => s.Shift.DayOffset >= startOffset && s.Shift.DayOffset <= endOffset);

        if (departmentId.HasValue)
        {
            var deptId = departmentId.Value;
            query = query.Where(s => s.Shift.Rota.TeamId == deptId);
        }

        var raw = await query
            .Select(s => new
            {
                s.UserId,
                s.Shift.RotaId,
                TeamId = s.Shift.Rota.TeamId,
                s.Shift.DayOffset,
                s.Shift.StartTime,
                s.Shift.Duration,
                s.Shift.IsAllDay,
            })
            .ToListAsync(ct);

        if (raw.Count == 0) return [];

        // Team names are NOT resolved here: Teams is another section's table and a
        // Shifts repo must not query db.Teams (memory/architecture/no-cross-section-ef-joins.md).
        // The caller (VolunteerTrackingExportService) stitches TeamId → name via
        // IShiftManagementService.GetDepartmentsWithRotasAsync.
        var rows = new List<ConfirmedShiftRow>(raw.Count);
        foreach (var r in raw)
        {
            // Reconstruct a minimal Shift so we can reuse the canonical helpers.
            var shift = new Shift
            {
                RotaId = r.RotaId,
                DayOffset = r.DayOffset,
                StartTime = r.StartTime,
                Duration = r.Duration,
                IsAllDay = r.IsAllDay,
            };
            var startsAtUtc = shift.GetAbsoluteStart(settings);
            var endsAtUtc = shift.GetAbsoluteEnd(settings);
            if (startsAtUtc < rangeEndUtcExclusive && endsAtUtc > rangeStartUtc)
            {
                rows.Add(new ConfirmedShiftRow(
                    r.UserId,
                    r.TeamId,
                    startsAtUtc,
                    endsAtUtc));
            }
        }
        return rows;
    }
}
