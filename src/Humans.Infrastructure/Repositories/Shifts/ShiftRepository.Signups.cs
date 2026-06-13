using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Auth;
using Humans.Infrastructure.Repositories.Governance;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

/// <summary>
/// Signup-focused portion of <see cref="ShiftRepository"/>.
/// </summary>
/// <remarks>
/// Uses the Scoped <see cref="HumansDbContext"/> directly (not
/// <see cref="IDbContextFactory{TContext}"/>) — same pattern as
/// <see cref="RoleAssignmentRepository"/> and <see cref="ApplicationRepository"/>.
/// Because <see cref="ShiftSignupService"/>'s mutation paths are
/// multi-step (load, mutate, audit-log, save), a Scoped context lets all
/// steps participate in a single EF change-tracker, which is simpler than
/// juggling per-method contexts in the service.
/// </remarks>
internal sealed partial class ShiftRepository
{
    // ============================================================
    // Reads — ShiftSignup
    // ============================================================

    public async Task<IReadOnlyList<ShiftSignup>> GetForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid? eventSettingsId = null,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];

        var query = _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Where(d => userIds.Contains(d.UserId));

        if (eventSettingsId.HasValue)
            query = query.Where(d => d.Shift.Rota.EventSettingsId == eventSettingsId.Value);

        // No display ordering here — every consumer either re-sorts for display
        // (ShiftSignupBucketer orders by AbsoluteStart; GetNoShow/Contribute order
        // by ReviewedAt/CreatedAt) or treats the result as an unordered set
        // (dict/HashSet/Any/Count). Display ordering belongs at the presentation layer.
        return await query.ToListAsync(ct);
    }

    public Task<ShiftSignup?> GetTeamProbeAsync(
        Guid id, ShiftSignupTeamProbeScope scope, CancellationToken ct = default)
    {
        var query = _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(d => d.Shift).ThenInclude(s => s.Rota);

        return scope switch
        {
            ShiftSignupTeamProbeScope.Signup => query.FirstOrDefaultAsync(d => d.Id == id, ct),
            ShiftSignupTeamProbeScope.SignupBlock => query.FirstOrDefaultAsync(d => d.SignupBlockId == id, ct),
            _ => Task.FromResult<ShiftSignup?>(null)
        };
    }

    public Task<ShiftSignup?> GetByIdForMutationAsync(Guid signupId, CancellationToken ct = default) =>
        _dbContext.ShiftSignups
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(d => d.Shift).ThenInclude(s => s.ShiftSignups)
            .FirstOrDefaultAsync(d => d.Id == signupId, ct);

    public async Task<List<ShiftSignup>> GetBlockForMutationAsync(
        Guid signupBlockId,
        ShiftSignupBlockMutationScope scope,
        CancellationToken ct = default)
    {
        var query = _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.Shift).ThenInclude(s => s.ShiftSignups)
            .Where(s => s.SignupBlockId == signupBlockId);

        query = scope == ShiftSignupBlockMutationScope.PendingAndConfirmed
            ? query.Where(s => s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending)
            : query.Where(s => s.Status == SignupStatus.Pending);

        return await query.ToListAsync(ct);
    }

    public async Task<HashSet<Guid>> GetActiveShiftIdsForUserAsync(
        Guid userId, IReadOnlyCollection<Guid> shiftIds, CancellationToken ct = default)
    {
        if (shiftIds.Count == 0)
            return [];

        return await _dbContext.ShiftSignups
            .AsNoTracking()
            .Where(s => s.UserId == userId && shiftIds.Contains(s.ShiftId) &&
                        (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed))
            .Select(s => s.ShiftId)
            .ToHashSetAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsForDayAsync(
        Guid eventSettingsId,
        int dayOffset,
        ShiftDayUserStatusScope statusScope,
        CancellationToken ct = default)
    {
        var query = _dbContext.ShiftSignups
            .AsNoTracking()
            .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId
                && s.Shift.DayOffset == dayOffset);

        query = statusScope switch
        {
            ShiftDayUserStatusScope.ConfirmedOnly => query.Where(s => s.Status == SignupStatus.Confirmed),
            ShiftDayUserStatusScope.PendingOrConfirmed => query.Where(s =>
                s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed),
            _ => query.Where(_ => false)
        };

        return await query
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    // ============================================================
    // Reads - signup-adjacent Shifts data
    // ============================================================

    public async Task<IReadOnlyList<VolunteerTagPreference>> GetVolunteerTagPreferencesForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        return await _dbContext.VolunteerTagPreferences
            .AsNoTracking()
            .Include(vtp => vtp.ShiftTag)
            .Where(vtp => userIds.Contains(vtp.UserId))
            .ToListAsync(ct);
    }

    // ============================================================
    // Writes — ShiftSignup
    // ============================================================

    public void AddRange(IEnumerable<ShiftSignup> signups) => _dbContext.ShiftSignups.AddRange(signups);

    public Task SaveChangesAsync(CancellationToken ct = default) => _dbContext.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default)
    {
        var activeSignups = await _dbContext.ShiftSignups
            .Where(d => d.UserId == userId &&
                        (d.Status == SignupStatus.Confirmed || d.Status == SignupStatus.Pending))
            .ToListAsync(ct);

        if (activeSignups.Count == 0)
            return [];

        var cancelled = new List<(Guid SignupId, Guid ShiftId)>(activeSignups.Count);
        foreach (var signup in activeSignups)
        {
            signup.Cancel(_clock, reason);
            cancelled.Add((signup.Id, signup.ShiftId));
        }

        await _dbContext.SaveChangesAsync(ct);
        return cancelled;
    }

    public Task<int> DeleteAllForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return Task.FromResult(0);

        return _dbContext.ShiftSignups
            .Where(s => userIds.Contains(s.UserId))
            .ExecuteDeleteAsync(ct);
    }

    // ============================================================
    // Account-merge fold
    // ============================================================

    public async Task<int> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        var sourceRows = await _dbContext.ShiftSignups
            .Where(s => s.UserId == sourceUserId)
            .ToListAsync(ct);

        var targetShiftIds = await _dbContext.ShiftSignups
            .Where(s => s.UserId == targetUserId)
            .Select(s => s.ShiftId)
            .ToListAsync(ct);
        var targetShiftIdSet = new HashSet<Guid>(targetShiftIds);

        foreach (var src in sourceRows)
        {
            if (targetShiftIdSet.Contains(src.ShiftId))
            {
                // Defensive: target already has a signup for this shift.
                // Drop the source row — target's slot stands.
                _dbContext.ShiftSignups.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        return sourceRows.Count;
    }

    public async Task<IReadOnlyList<ShiftSignup>> GetAllForOrphanScanAsync(CancellationToken ct = default)
    {
        return await _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(s => s.Shift)
                .ThenInclude(sh => sh.Rota)
                    .ThenInclude(r => r.EventSettings)
            .OrderBy(s => s.CreatedAt) // arch:db-sort-ok orphan-scan deterministic order (maintenance job, no UI)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        var userIds = await _dbContext.ShiftSignups
            .AsNoTracking()
            .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId
                     && (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed))
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(ct);
        return userIds.ToHashSet();
    }

    public async Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        var es = await _dbContext.EventSettings
            .Where(x => x.Id == eventSettingsId)
            .Select(x => new { x.BuildStartOffset })
            .FirstOrDefaultAsync(ct);

        if (es is null) return [];

        // SignupStatus and RotaPeriod are stored as strings via
        // HasConversion<string>(). Per memory/code/no-enum-compare-in-ef.md, we
        // avoid `>=`/`<=` on those enums and use an explicit `||` chain so the
        // SQL stays a literal-IN match (no lexicographic comparison).
        // DayOffset is an int — direct numeric comparison is safe.
        return await _dbContext.ShiftSignups
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
        var settings = await _dbContext.EventSettings
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

        var query = _dbContext.ShiftSignups
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
                s.Shift.Rota.TeamId,
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
