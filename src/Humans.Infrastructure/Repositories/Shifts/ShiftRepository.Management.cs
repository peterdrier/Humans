using Humans.Application.Architecture;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

/// <summary>
/// EF-backed repository for the Shifts section. One concrete repository backs
/// the management and signup repository interfaces so rota, shift, volunteer
/// profile, tag preference, and signup access stays behind a single persistence
/// adapter.
///
/// <para>
/// Management methods create short-lived contexts through
/// <see cref="IDbContextFactory{TContext}"/>. Signup mutation methods use the
/// scoped <see cref="HumansDbContext"/> so multi-step load/mutate/save flows
/// share one EF change tracker.
/// </para>
/// </summary>
[Grandfathered("HUM0025", justification: "EventSettings is also read by VolunteerTrackingRepository; route VolunteerTracking through a Shifts read surface.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "EventSettings")]
[Grandfathered("HUM0025", justification: "ShiftSignups is also read by VolunteerTrackingRepository; route VolunteerTracking through a Shifts read surface.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "ShiftSignups")]
internal sealed partial class ShiftRepository : IShiftManagementRepository, IShiftSignupRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public ShiftRepository(
        IDbContextFactory<HumansDbContext> factory,
        HumansDbContext dbContext,
        IClock clock)
    {
        _factory = factory;
        _dbContext = dbContext;
        _clock = clock;
    }

    // ==========================================================================
    // EventSettings
    // ==========================================================================

    public async Task<EventSettings?> GetActiveEventSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .FirstOrDefaultAsync(e => e.IsActive, ct);
    }

    public async Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<bool> AnyOtherActiveEventSettingsAsync(Guid? excludingId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.EventSettings.AsNoTracking().Where(e => e.IsActive);
        if (excludingId.HasValue)
            query = query.Where(e => e.Id != excludingId.Value);
        return await query.AnyAsync(ct);
    }

    public async Task AddEventSettingsAsync(EventSettings entity, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.EventSettings.Add(entity);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateEventSettingsAsync(EventSettings entity, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.EventSettings.Update(entity);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteEventCascadeAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);

        var rotaIds = await ctx.Rotas
            .Where(r => r.EventSettingsId == eventSettingsId)
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (rotaIds.Count > 0)
        {
            var shiftIds = await ctx.Shifts
                .Where(s => rotaIds.Contains(s.RotaId))
                .Select(s => s.Id)
                .ToListAsync(ct);

            if (shiftIds.Count > 0)
            {
                await ctx.ShiftSignups
                    .Where(s => shiftIds.Contains(s.ShiftId))
                    .ExecuteDeleteAsync(ct);

                await ctx.Shifts
                    .Where(s => shiftIds.Contains(s.Id))
                    .ExecuteDeleteAsync(ct);
            }

            await ctx.Rotas
                .Where(r => rotaIds.Contains(r.Id))
                .ExecuteDeleteAsync(ct);
        }

        var deleted = await ctx.EventSettings
            .Where(e => e.Id == eventSettingsId)
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return deleted;
    }

    // ==========================================================================
    // Rota
    // ==========================================================================

    public async Task AddRotaAsync(Rota rota, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Rotas.Add(rota);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateRotaAsync(Rota rota, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Rotas.Update(rota);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateRotaTeamAssignmentAsync(
        Guid rotaId, Guid newTeamId, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rota = await ctx.Rotas
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);
        if (rota is null) return false;

        // Targeted property update: only TeamId + UpdatedAt are flagged as
        // modified, so concurrent edits to other Rota columns (Name, Period,
        // etc.) by other admins are not silently overwritten on SaveChanges.
        rota.TeamId = newTeamId;
        rota.UpdatedAt = updatedAt;
        ctx.Entry(rota).Property(r => r.TeamId).IsModified = true;
        ctx.Entry(rota).Property(r => r.UpdatedAt).IsModified = true;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Rota?> GetRotaForUpdateAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rota = await ctx.Rotas
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);
        if (rota is null) return null;

        // Detach: the caller typically mutates simple fields and then calls
        // UpdateRotaAsync on a fresh context.
        ctx.Entry(rota).State = EntityState.Detached;
        return rota;
    }

    public async Task<Rota?> GetRotaWithShiftsAndSignupsForDeleteAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);
    }

    public async Task<Rota?> GetRotaByIdWithShiftsAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .AsNoTracking()
            .Include(r => r.Shifts)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);
    }

    public async Task<Rota?> GetRotaForViewAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .AsNoTracking()
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
            .Include(r => r.Tags)
            .Include(r => r.EventSettings)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);
    }

    public async Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(
        Guid teamId, Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .AsNoTracking()
            .Include(r => r.EventSettings)
            .Include(r => r.Tags)
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
            .Where(r => r.TeamId == teamId && r.EventSettingsId == eventSettingsId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<Rota?> GetRotaWithEventSettingsAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .AsNoTracking()
            .Include(r => r.EventSettings)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);
    }

    public async Task DeleteRotaCascadeAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rota = await ctx.Rotas
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);

        if (rota is null) return;

        // ShiftSignup→Shift FK is Restrict, so cascade won't handle them — delete explicitly.
        foreach (var shift in rota.Shifts)
        {
            ctx.ShiftSignups.RemoveRange(shift.ShiftSignups);
        }

        ctx.Rotas.Remove(rota);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Rota>> SearchRotasAsync(
        string query, Guid eventSettingsId, bool onlyVolunteerVisible,
        int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return [];

        var pattern = "%" + EscapeLikePattern(query.Trim()) + "%";

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var q = ctx.Rotas
            .AsNoTracking()
            .Where(r => r.EventSettingsId == eventSettingsId
                && EF.Functions.ILike(r.Name, pattern, "\\"));

        if (onlyVolunteerVisible)
            q = q.Where(r => r.IsVisibleToVolunteers);

        return await q
            // Deterministic Take(max) for global search; controller re-ranks by score before display.
            .OrderBy(r => r.Name) // arch:db-sort-ok
            .Take(max)
            .ToListAsync(ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public async Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rota = await ctx.Rotas
            .Include(r => r.Tags)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);

        if (rota is null) return;

        rota.Tags.Clear();

        if (tagIds.Count > 0)
        {
            var tags = await ctx.ShiftTags
                .Where(t => tagIds.Contains(t.Id))
                .ToListAsync(ct);

            foreach (var tag in tags)
            {
                rota.Tags.Add(tag);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Shift
    // ==========================================================================

    public async Task AddShiftAsync(Shift shift, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddShiftsAsync(IEnumerable<Shift> shifts, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Shifts.AddRange(shifts);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateShiftAsync(Shift shift, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Shifts.Update(shift);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<Shift?> GetShiftWithSignupsForDeleteAsync(Guid shiftId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Shifts
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct);
    }

    public async Task DeleteShiftCascadeAsync(Guid shiftId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var shift = await ctx.Shifts
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct);

        if (shift is null) return;

        ctx.ShiftSignups.RemoveRange(shift.ShiftSignups);
        ctx.Shifts.Remove(shift);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<Shift?> GetShiftByIdAsync(Guid shiftId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Shifts
            .AsNoTracking()
            .Include(s => s.Rota)
                .ThenInclude(r => r.EventSettings)
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct);
    }

    public async Task<IReadOnlyList<Shift>> GetShiftsByRotaAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Shifts
            .AsNoTracking()
            .Include(s => s.ShiftSignups)
            .Where(s => s.RotaId == rotaId)
            .OrderBy(s => s.DayOffset)
            .ThenBy(s => s.StartTime)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<int>> GetShiftDayOffsetsForRotaAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Shifts
            .AsNoTracking()
            .Where(s => s.RotaId == rotaId)
            .Select(s => s.DayOffset)
            .Distinct()
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Reads for dashboards / urgency / staffing
    // ==========================================================================

    public async Task<IReadOnlyList<Shift>> GetShiftsForEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? departmentTeamIds,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.Shifts
            .AsNoTracking()
            .Include(s => s.Rota)
                .ThenInclude(r => r.EventSettings)
            .Where(s => s.Rota.EventSettingsId == eventSettingsId);

        if (departmentTeamIds is { Count: > 0 })
            query = query.Where(s => departmentTeamIds.Contains(s.Rota.TeamId));

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Shift>> GetVisibleShiftsForEventAsync(
        Guid eventSettingsId,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Shifts
            .AsNoTracking()
            .Include(s => s.Rota)
            .Where(s => s.Rota.EventSettingsId == eventSettingsId
                        && !s.AdminOnly
                        && s.Rota.IsVisibleToVolunteers)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Shift>> GetShiftsWithSignupsForEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? departmentTeamIds,
        bool includeAdminOnly,
        bool includeHidden,
        int? fromDayOffset,
        int? toDayOffset,
        bool includeRotaTags,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        IQueryable<Shift> query = ctx.Shifts
            .AsNoTracking()
            .Include(s => s.Rota)
                .ThenInclude(r => r.EventSettings)
            .Include(s => s.ShiftSignups);

        if (includeRotaTags)
        {
            query = ctx.Shifts
                .AsNoTracking()
                .Include(s => s.Rota)
                    .ThenInclude(r => r.EventSettings)
                .Include(s => s.Rota)
                    .ThenInclude(r => r.Tags)
                .Include(s => s.ShiftSignups);
        }

        query = query.Where(s => s.Rota.EventSettingsId == eventSettingsId);

        if (!includeAdminOnly)
            query = query.Where(s => !s.AdminOnly);

        if (!includeHidden)
            query = query.Where(s => s.Rota.IsVisibleToVolunteers);

        if (departmentTeamIds is { Count: > 0 })
            query = query.Where(s => departmentTeamIds.Contains(s.Rota.TeamId));

        if (fromDayOffset.HasValue)
        {
            var fromVal = fromDayOffset.Value;
            query = query.Where(s => s.DayOffset >= fromVal);
        }

        if (toDayOffset.HasValue)
        {
            var toVal = toDayOffset.Value;
            query = query.Where(s => s.DayOffset <= toVal);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Shift>> GetShiftsWithSignupsForUrgencyAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? departmentTeamIds,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.Shifts
            .AsNoTracking()
            .Include(s => s.Rota)
                .ThenInclude(r => r.EventSettings)
            .Include(s => s.ShiftSignups)
            .Where(s => s.Rota.EventSettingsId == eventSettingsId);

        if (departmentTeamIds is { Count: > 0 })
            query = query.Where(s => departmentTeamIds.Contains(s.Rota.TeamId));

        if (minDayOffset.HasValue)
        {
            var minv = minDayOffset.Value;
            query = query.Where(s => s.DayOffset >= minv);
        }
        if (maxDayOffset.HasValue)
        {
            var maxv = maxDayOffset.Value;
            query = query.Where(s => s.DayOffset <= maxv);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Rota>> GetRotasWithShiftsAndSignupsAsync(
        Guid eventSettingsId,
        IReadOnlyList<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .AsNoTracking()
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
            .Where(r => r.EventSettingsId == eventSettingsId && teamIds.Contains(r.TeamId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetTeamIdsWithRotasInEventAsync(
        Guid eventSettingsId,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .AsNoTracking()
            .Where(r => r.EventSettingsId == eventSettingsId)
            .Select(r => r.TeamId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetTeamIdsWithShiftsInEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Rotas
            .AsNoTracking()
            .Where(r => r.EventSettingsId == eventSettingsId
                && teamIds.Contains(r.TeamId)
                && r.Shifts.Any())
            .Select(r => r.TeamId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByShiftAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default)
    {
        if (shiftIds.Count == 0)
            return new Dictionary<Guid, int>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftSignups
            .AsNoTracking()
            .Where(su => shiftIds.Contains(su.ShiftId) && su.Status == SignupStatus.Confirmed)
            .GroupBy(su => su.ShiftId)
            .Select(g => new { ShiftId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ShiftId, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetEngagedUserIdsForShiftsAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default)
    {
        if (shiftIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftSignups
            .AsNoTracking()
            .Where(su => shiftIds.Contains(su.ShiftId) && su.Status != SignupStatus.Cancelled)
            .Select(su => su.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<int> GetStalePendingSignupCountAsync(
        IReadOnlyCollection<Guid> shiftIds,
        Instant staleThreshold,
        CancellationToken ct = default)
    {
        if (shiftIds.Count == 0) return 0;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftSignups
            .AsNoTracking()
            .CountAsync(su => shiftIds.Contains(su.ShiftId)
                              && su.Status == SignupStatus.Pending
                              && su.CreatedAt < staleThreshold, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetPendingSignupCountsByTeamAsync(
        Guid eventSettingsId,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query =
            from rota in ctx.Rotas
            where rota.EventSettingsId == eventSettingsId
            join shift in ctx.Shifts on rota.Id equals shift.RotaId
            join signup in ctx.ShiftSignups on shift.Id equals signup.ShiftId
            where signup.Status == SignupStatus.Pending
            select new { rota.TeamId, shift.DayOffset, BlockKey = signup.SignupBlockId ?? signup.Id };

        if (minDayOffset.HasValue)
        {
            var minv = minDayOffset.Value;
            query = query.Where(x => x.DayOffset >= minv);
        }
        if (maxDayOffset.HasValue)
        {
            var maxv = maxDayOffset.Value;
            query = query.Where(x => x.DayOffset <= maxv);
        }

        return await query
            .Select(x => new { x.TeamId, x.BlockKey })
            .Distinct()
            .GroupBy(x => x.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<Instant>> GetSignupCreatedAtsInWindowAsync(
        Guid eventSettingsId,
        Instant fromInclusive,
        Instant toExclusive,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query =
            from su in ctx.ShiftSignups.AsNoTracking()
            join sh in ctx.Shifts.AsNoTracking() on su.ShiftId equals sh.Id
            join r in ctx.Rotas.AsNoTracking() on sh.RotaId equals r.Id
            where r.EventSettingsId == eventSettingsId
                  && su.CreatedAt >= fromInclusive
                  && su.CreatedAt < toExclusive
            select new { su.CreatedAt, sh.DayOffset };

        if (minDayOffset.HasValue)
        {
            var minv = minDayOffset.Value;
            query = query.Where(x => x.DayOffset >= minv);
        }
        if (maxDayOffset.HasValue)
        {
            var maxv = maxDayOffset.Value;
            query = query.Where(x => x.DayOffset <= maxv);
        }

        return await query.Select(x => x.CreatedAt).ToListAsync(ct);
    }

    public async Task<Guid> GetActiveEventIdAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings
            .AsNoTracking()
            .Where(e => e.IsActive && e.Id == eventSettingsId)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ShiftSignup>> GetUserActiveSignupsForCantinaGateAsync(
        Guid userId,
        Guid eventSettingsId,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftSignups
            .AsNoTracking()
            .Include(s => s.Shift)
            .Where(s => s.UserId == userId
                && (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed)
                && s.Shift!.Rota!.EventSettingsId == eventSettingsId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetOnSiteUserIdsForDayAsync(
        int dayOffset, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftSignups
            .AsNoTracking()
            .Where(ss => (ss.Status == SignupStatus.Pending || ss.Status == SignupStatus.Confirmed)
                      && ss.Shift!.DayOffset == dayOffset)
            .Select(ss => ss.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Shift tags
    // ==========================================================================

    public async Task<IReadOnlyList<ShiftTag>> GetTagsAsync(string? query = null, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var tags = ctx.ShiftTags.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
            tags = tags.Where(t => EF.Functions.ILike(t.Name, $"%{query}%"));

        return await tags.ToListAsync(ct);
    }

    public async Task<ShiftTag?> FindTagByNameAsync(string name, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftTags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => EF.Functions.ILike(t.Name, name), ct);
    }

    public async Task AddTagAsync(ShiftTag tag, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.ShiftTags.Add(tag);
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Volunteer tag preferences
    // ==========================================================================

    public async Task<IReadOnlyList<ShiftTag>> GetVolunteerTagPreferencesAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.VolunteerTagPreferences
            .AsNoTracking()
            .Where(v => v.UserId == userId)
            .Select(v => v.ShiftTag)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task SetVolunteerTagPreferencesAsync(
        Guid userId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.VolunteerTagPreferences
            .Where(v => v.UserId == userId)
            .ToListAsync(ct);

        ctx.VolunteerTagPreferences.RemoveRange(existing);

        foreach (var tagId in tagIds)
        {
            ctx.VolunteerTagPreferences.Add(new VolunteerTagPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftTagId = tagId
            });
        }

        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Volunteer event profiles
    // ==========================================================================

    public async Task<VolunteerEventProfile?> GetVolunteerEventProfileForUpdateAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profile = await ctx.VolunteerEventProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null) return null;
        ctx.Entry(profile).State = EntityState.Detached;
        return profile;
    }

    public async Task<VolunteerEventProfile?> GetVolunteerEventProfileAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.VolunteerEventProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<VolunteerEventProfile>> GetVolunteerEventProfilesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.VolunteerEventProfiles
            .AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync(ct);
    }

    public async Task AddVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.VolunteerEventProfiles.Add(profile);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.VolunteerEventProfiles.Update(profile);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteVolunteerEventProfilesForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var profiles = await ctx.VolunteerEventProfiles
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);

        if (profiles.Count == 0)
            return 0;

        ctx.VolunteerEventProfiles.RemoveRange(profiles);
        await ctx.SaveChangesAsync(ct);
        return profiles.Count;
    }

    // ==========================================================================
    // Account-merge fold
    // ==========================================================================

    public async Task<int> ReassignProfilesAndTagPrefsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // VolunteerEventProfile is 1:1 with User (unique index on UserId).
        // Source has at most one row; target has at most one row.
        var sourceProfiles = await ctx.VolunteerEventProfiles
            .Where(p => p.UserId == sourceUserId)
            .ToListAsync(ct);

        var targetHasProfile = await ctx.VolunteerEventProfiles
            .AnyAsync(p => p.UserId == targetUserId, ct);

        foreach (var src in sourceProfiles)
        {
            if (targetHasProfile)
            {
                // Target already has a profile — target wins, drop source.
                ctx.VolunteerEventProfiles.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
                // After re-FK'ing the first (and only) source row, any
                // additional source rows would now collide; defensive guard.
                targetHasProfile = true;
            }
        }

        // VolunteerTagPreference uniqueness is on (UserId, ShiftTagId).
        var sourceTagPrefs = await ctx.VolunteerTagPreferences
            .Where(v => v.UserId == sourceUserId)
            .ToListAsync(ct);

        var targetTagIds = await ctx.VolunteerTagPreferences
            .Where(v => v.UserId == targetUserId)
            .Select(v => v.ShiftTagId)
            .ToListAsync(ct);
        var targetTagIdSet = new HashSet<Guid>(targetTagIds);

        foreach (var src in sourceTagPrefs)
        {
            if (targetTagIdSet.Contains(src.ShiftTagId))
            {
                // Target already prefers this tag — target wins, drop source.
                ctx.VolunteerTagPreferences.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                // VolunteerTagPreference has no UpdatedAt; updatedAt is
                // unused for this table.
                targetTagIdSet.Add(src.ShiftTagId);
            }
        }

        await ctx.SaveChangesAsync(ct);

        var profileCount = await ctx.VolunteerEventProfiles
            .CountAsync(p => p.UserId == targetUserId, ct);
        var tagPrefCount = await ctx.VolunteerTagPreferences
            .CountAsync(v => v.UserId == targetUserId, ct);

        return profileCount + tagPrefCount;
    }
}

