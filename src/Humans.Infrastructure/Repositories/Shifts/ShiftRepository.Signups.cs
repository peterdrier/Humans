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
            .OrderBy(s => s.CreatedAt)
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
}
