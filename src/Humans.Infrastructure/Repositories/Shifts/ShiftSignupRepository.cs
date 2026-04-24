using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Auth;
using Humans.Infrastructure.Repositories.Governance;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

/// <summary>
/// EF-backed implementation of <see cref="IShiftSignupRepository"/>. The only
/// non-test file that touches <c>DbContext.ShiftSignups</c> from the
/// <c>ShiftSignupService</c> migration onward.
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
public sealed class ShiftSignupRepository : IShiftSignupRepository
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public ShiftSignupRepository(HumansDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    // ============================================================
    // Reads — ShiftSignup
    // ============================================================

    public Task<bool> HasActiveSignupAsync(Guid userId, Guid shiftId, CancellationToken ct = default) =>
        _dbContext.ShiftSignups
            .AsNoTracking()
            .AnyAsync(s => s.UserId == userId && s.ShiftId == shiftId &&
                           (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed),
                ct);

    // Cross-domain .Include(Rota.Team) / .Include(ShiftSignup.User) / .Include(ShiftSignup.ReviewedByUser)
    // are retained here for now as a pragmatic shortcut inside the Shifts repository
    // (design-rules §6 forbids cross-domain includes in the Application layer — which
    // is already clean — but the repository still eager-loads these for view compatibility).
    // The nav props are [Obsolete] for Application-layer consumers (§6c); the suppressions
    // below scope the warning to just these legacy includes. Retiring them lands in the
    // wider User-entity nav-strip follow-up.
#pragma warning disable CS0618

    public async Task<IReadOnlyList<ShiftSignup>> GetByUserAsync(
        Guid userId, Guid? eventSettingsId = null, CancellationToken ct = default)
    {
        var query = _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Where(d => d.UserId == userId);

        if (eventSettingsId.HasValue)
            query = query.Where(d => d.Shift.Rota.EventSettingsId == eventSettingsId.Value);

        return await query.OrderBy(d => d.Shift.DayOffset).ThenBy(d => d.Shift.StartTime).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ShiftSignup>> GetActiveSignupsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Where(d => d.UserId == userId &&
                        (d.Status == SignupStatus.Confirmed || d.Status == SignupStatus.Pending))
            .ToListAsync(ct);
    }

    public Task<ShiftSignup?> GetByIdAsync(Guid signupId, CancellationToken ct = default) =>
        _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .FirstOrDefaultAsync(d => d.Id == signupId, ct);

    public Task<ShiftSignup?> GetByIdForMutationAsync(Guid signupId, CancellationToken ct = default) =>
        _dbContext.ShiftSignups
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(d => d.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Include(d => d.Shift).ThenInclude(s => s.ShiftSignups)
            .FirstOrDefaultAsync(d => d.Id == signupId, ct);

    public async Task<List<ShiftSignup>> GetBlockForMutationAsync(
        Guid signupBlockId, bool includeConfirmed, CancellationToken ct = default)
    {
        var query = _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.Shift).ThenInclude(s => s.Rota).ThenInclude(r => r.Team)
            .Include(s => s.Shift).ThenInclude(s => s.ShiftSignups)
            .Where(s => s.SignupBlockId == signupBlockId);

        query = includeConfirmed
            ? query.Where(s => s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending)
            : query.Where(s => s.Status == SignupStatus.Pending);

        return await query.ToListAsync(ct);
    }
#pragma warning restore CS0618

    public Task<ShiftSignup?> GetByBlockIdFirstAsync(Guid signupBlockId, CancellationToken ct = default) =>
        _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(s => s.Shift).ThenInclude(s => s.Rota)
            .FirstOrDefaultAsync(s => s.SignupBlockId == signupBlockId, ct);

#pragma warning disable CS0618 // Cross-domain .Include(User) / .Include(ReviewedByUser) — see file header note
    public async Task<IReadOnlyList<ShiftSignup>> GetByShiftAsync(Guid shiftId, CancellationToken ct = default) =>
        await _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(d => d.User)
            .Include(d => d.Shift)
                .ThenInclude(s => s.Rota)
            .Where(d => d.ShiftId == shiftId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ShiftSignup>> GetNoShowHistoryAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(s => s.Shift).ThenInclude(sh => sh.Rota).ThenInclude(r => r.Team)
            .Include(s => s.Shift).ThenInclude(sh => sh.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.ReviewedByUser)
            .Where(s => s.UserId == userId && s.Status == SignupStatus.NoShow)
            .OrderByDescending(s => s.ReviewedAt)
            .ToListAsync(ct);
#pragma warning restore CS0618

    public async Task<HashSet<Guid>> GetActiveShiftIdsForUserAsync(
        Guid userId, IReadOnlyCollection<Guid> shiftIds, CancellationToken ct = default)
    {
        if (shiftIds.Count == 0)
            return new HashSet<Guid>();

        return await _dbContext.ShiftSignups
            .AsNoTracking()
            .Where(s => s.UserId == userId && shiftIds.Contains(s.ShiftId) &&
                        (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed))
            .Select(s => s.ShiftId)
            .ToHashSetAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetConfirmedCountsByShiftAsync(
        IReadOnlyCollection<Guid> shiftIds, CancellationToken ct = default)
    {
        if (shiftIds.Count == 0)
            return new Dictionary<Guid, int>();

        return await _dbContext.ShiftSignups
            .AsNoTracking()
            .Where(s => shiftIds.Contains(s.ShiftId) && s.Status == SignupStatus.Confirmed)
            .GroupBy(s => s.ShiftId)
            .Select(g => new { ShiftId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ShiftId, g => g.Count, ct);
    }

    public Task<int> GetDistinctEeUsersOnDayAsync(
        Guid eventSettingsId, int dayOffset, CancellationToken ct = default) =>
        _dbContext.ShiftSignups
            .AsNoTracking()
            .Where(d => d.Status == SignupStatus.Confirmed &&
                        d.Shift.Rota.EventSettingsId == eventSettingsId &&
                        d.Shift.DayOffset == dayOffset)
            .Select(d => d.UserId)
            .Distinct()
            .CountAsync(ct);

    public async Task<IReadOnlyList<ShiftSignup>> GetForGdprExportAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.ShiftSignups
            .AsNoTracking()
            .Include(ss => ss.Shift)
                .ThenInclude(s => s.Rota)
                    .ThenInclude(r => r.EventSettings)
            .Where(ss => ss.UserId == userId)
            .OrderByDescending(ss => ss.CreatedAt)
            .ToListAsync(ct);

    // ============================================================
    // Reads — within-section cross-service (pending #541a / #541c)
    // ============================================================

#pragma warning disable CS0618 // Cross-domain .Include(Rota.Team) — see file header note
    public Task<Shift?> GetShiftWithContextAsync(Guid shiftId, CancellationToken ct = default) =>
        _dbContext.Shifts
            .AsNoTracking()
            .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.Rota).ThenInclude(r => r.Team)
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct);
#pragma warning restore CS0618

    public Task<Rota?> GetRotaWithShiftsAsync(Guid rotaId, CancellationToken ct = default) =>
        _dbContext.Rotas
            .AsNoTracking()
            .Include(r => r.EventSettings)
            .Include(r => r.Shifts)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);

    public async Task<IReadOnlyList<VolunteerEventProfile>> GetVolunteerEventProfilesForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.VolunteerEventProfiles
            .AsNoTracking()
            .Where(vep => vep.UserId == userId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<GeneralAvailability>> GetGeneralAvailabilityForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.GeneralAvailability
            .AsNoTracking()
            .Include(ga => ga.EventSettings)
            .Where(ga => ga.UserId == userId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VolunteerTagPreference>> GetVolunteerTagPreferencesForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.VolunteerTagPreferences
            .AsNoTracking()
            .Include(vtp => vtp.ShiftTag)
            .Where(vtp => vtp.UserId == userId)
            .ToListAsync(ct);

    // ============================================================
    // Writes — ShiftSignup
    // ============================================================

    public void Add(ShiftSignup signup) => _dbContext.ShiftSignups.Add(signup);

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
            return Array.Empty<(Guid, Guid)>();

        var cancelled = new List<(Guid SignupId, Guid ShiftId)>(activeSignups.Count);
        foreach (var signup in activeSignups)
        {
            signup.Cancel(_clock, reason);
            cancelled.Add((signup.Id, signup.ShiftId));
        }

        await _dbContext.SaveChangesAsync(ct);
        return cancelled;
    }

}
