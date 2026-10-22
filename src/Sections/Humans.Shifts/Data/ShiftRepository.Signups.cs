using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Shifts.Data;

/// <summary>
/// Uses the scoped <see cref="ShiftsDbContext"/>, not the factory, so a multi-step mutation (load, mutate, audit,
/// save) shares one change tracker.
/// </summary>
internal sealed partial class ShiftRepository
{
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

        // Unordered on purpose: every consumer re-sorts for display or treats the result as a set.
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

    public async Task<int> DeleteVolunteerTagPreferencesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var preferences = await _dbContext.VolunteerTagPreferences
            .Where(vtp => vtp.UserId == userId)
            .ToListAsync(ct);
        if (preferences.Count == 0) return 0;

        _dbContext.VolunteerTagPreferences.RemoveRange(preferences);
        await _dbContext.SaveChangesAsync(ct);
        return preferences.Count;
    }

    public async Task<int> ClearSignupStatusReasonsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var signups = await _dbContext.ShiftSignups
            .Where(ss => ss.UserId == userId && ss.StatusReason != null)
            .ToListAsync(ct);
        if (signups.Count == 0) return 0;

        foreach (var signup in signups)
            signup.StatusReason = null;

        await _dbContext.SaveChangesAsync(ct);
        return signups.Count;
    }

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
                // The target already holds this shift; its slot stands.
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

    public async Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, int buildStartOffset, CancellationToken ct = default)
    {
        // The enums are stored as strings, so an explicit || chain keeps the SQL a literal IN, never a lexicographic
        // >= (memory/code/no-enum-compare-in-ef.md).
        return await _dbContext.ShiftSignups
            .Where(s => s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending)
            .Where(s => s.Shift.DayOffset >= buildStartOffset && s.Shift.DayOffset < 0)
            .Where(s => s.Shift.Rota.Period == RotaPeriod.Build || s.Shift.Rota.Period == RotaPeriod.All)
            .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId)
            .Select(s => new EligibleBuildSignup(
                s.UserId, s.Shift.DayOffset, s.Status, s.Shift.Rota.Name))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
        Guid eventSettingsId,
        IEventSettingsInfo calendar,
        LocalDate startDate,
        LocalDate endDate,
        Guid? departmentId,
        CancellationToken ct)
    {
        // Absolute times are not columns: they need a NodaTime zone conversion SQL can't do. So the event's confirmed
        // rows load and overlap is checked in memory; a shift can span days, so a DayOffset buffer would miss work.
        var settings = calendar;

        var zone = DateTimeZoneProviders.Tzdb[settings.TimeZoneId];
        var rangeStartUtc = startDate.AtStartOfDayInZone(zone).ToInstant();
        var rangeEndUtcExclusive = endDate.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();

        var query = _dbContext.ShiftSignups
            .AsNoTracking()
            .Where(s => s.Status == SignupStatus.Confirmed)
            .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId);

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

        // Team names are another section's table; the caller stitches them in.
        var rows = new List<ConfirmedShiftRow>(raw.Count);
        foreach (var r in raw)
        {
            // A minimal Shift, to reuse the canonical time helpers.
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
