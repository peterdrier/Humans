using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Shifts.Contracts;
namespace Humans.Shifts.Data;

/// <summary>
/// Management methods use short-lived contexts from the factory; signup mutations use the scoped
/// <see cref="ShiftsDbContext"/> so load/mutate/save flows share one change tracker.
/// </summary>
internal sealed partial class ShiftRepository : IShiftManagementRepository
{
    private readonly IDbContextFactory<ShiftsDbContext> _factory;
    private readonly ShiftsDbContext _dbContext;
    private readonly IClock _clock;

    public ShiftRepository(
        IDbContextFactory<ShiftsDbContext> factory,
        ShiftsDbContext dbContext,
        IClock clock)
    {
        _factory = factory;
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<IReadOnlyList<EventSettings>> GetAllEventSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings.AsNoTracking().ToListAsync(ct);
    }

    public async Task SaveEventSettingsAsync(EventSettings entity, EntityMutationMode mode, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (mode == EntityMutationMode.Add)
            ctx.EventSettings.Add(entity);
        else
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

    public async Task SaveRotaAsync(Rota rota, EntityMutationMode mode, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (mode == EntityMutationMode.Add)
            ctx.Rotas.Add(rota);
        else
            ctx.Entry(rota).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateRotaTeamAssignmentAsync(
        Guid rotaId, Guid newTeamId, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rota = await ctx.Rotas
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);
        if (rota is null) return false;

        // Only TeamId and UpdatedAt are flagged modified, so another admin's concurrent edit to other columns survives.
        rota.TeamId = newTeamId;
        rota.UpdatedAt = updatedAt;
        ctx.Entry(rota).Property(r => r.TeamId).IsModified = true;
        ctx.Entry(rota).Property(r => r.UpdatedAt).IsModified = true;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Rota?> GetRotaAsync(
        Guid rotaId, RotaReadShape shape, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        IQueryable<Rota> query = ctx.Rotas.AsNoTracking();

        if (shape.HasFlag(RotaReadShape.EventSettings))
            query = query.Include(r => r.EventSettings);

        if (shape.HasFlag(RotaReadShape.ShiftSignups))
            query = query.Include(r => r.Shifts).ThenInclude(s => s.ShiftSignups);
        else if (shape.HasFlag(RotaReadShape.Shifts))
            query = query.Include(r => r.Shifts);

        if (shape.HasFlag(RotaReadShape.Tags))
            query = query.Include(r => r.Tags);

        return await query.FirstOrDefaultAsync(r => r.Id == rotaId, ct);
    }

    public async Task DeleteRotaCascadeAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rota = await ctx.Rotas
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
            .FirstOrDefaultAsync(r => r.Id == rotaId, ct);

        if (rota is null) return;

        // The signup FK is Restrict, so cascade won't remove them.
        foreach (var shift in rota.Shifts)
        {
            ctx.ShiftSignups.RemoveRange(shift.ShiftSignups);
        }

        ctx.Rotas.Remove(rota);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Rota>> SearchVolunteerVisibleRotasAsync(
        string query, Guid eventSettingsId, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return [];

        var pattern = "%" + EscapeLikePattern(query.Trim()) + "%";

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var q = ctx.Rotas
            .AsNoTracking()
            .Where(r => r.EventSettingsId == eventSettingsId
                && r.IsVisibleToVolunteers
                && EF.Functions.ILike(r.Name, pattern, "\\"));

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

    public async Task SaveShiftAsync(Shift shift, EntityMutationMode mode, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (mode == EntityMutationMode.Add)
            ctx.Shifts.Add(shift);
        else
            ctx.Entry(shift).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddShiftsAsync(IEnumerable<Shift> shifts, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Shifts.AddRange(shifts);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<Shift?> GetShiftAsync(Guid shiftId, ShiftReadShape shape, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        IQueryable<Shift> query = ctx.Shifts.AsNoTracking();

        if (shape.HasFlag(ShiftReadShape.EventSettings))
            query = query.Include(s => s.Rota).ThenInclude(r => r.EventSettings);
        else if (shape.HasFlag(ShiftReadShape.Rota))
            query = query.Include(s => s.Rota);

        if (shape.HasFlag(ShiftReadShape.ShiftSignups))
            query = query.Include(s => s.ShiftSignups);

        return await query.FirstOrDefaultAsync(s => s.Id == shiftId, ct);
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

    public async Task<IReadOnlyList<Shift>> GetEventShiftsAsync(
        ShiftEventQuery request,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        IQueryable<Shift> query = ctx.Shifts
            .AsNoTracking()
            .Include(s => s.Rota);

        if (request.Flags.HasFlag(ShiftEventQueryFlags.IncludeRotaTags))
            query = query.Include(s => s.Rota).ThenInclude(r => r.Tags);

        if (request.Flags.HasFlag(ShiftEventQueryFlags.IncludeSignups))
            query = query.Include(s => s.ShiftSignups);

        query = query.Where(s => s.Rota.EventSettingsId == request.EventSettingsId);

        if (request.Flags.HasFlag(ShiftEventQueryFlags.ExcludeAdminOnly))
            query = query.Where(s => !s.AdminOnly);

        if (request.Flags.HasFlag(ShiftEventQueryFlags.ExcludeHiddenRotas))
            query = query.Where(s => s.Rota.IsVisibleToVolunteers);

        if (request.TeamIds is { Count: > 0 })
            query = query.Where(s => request.TeamIds.Contains(s.Rota.TeamId));

        if (request.MinDayOffset.HasValue)
        {
            var minv = request.MinDayOffset.Value;
            query = query.Where(s => s.DayOffset >= minv);
        }

        if (request.MaxDayOffset.HasValue)
        {
            var maxv = request.MaxDayOffset.Value;
            query = query.Where(s => s.DayOffset <= maxv);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Rota>> GetRotasAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        RotaReadShape shape,
        CancellationToken ct = default)
    {
        if (teamIds.Count == 0) return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.Rotas.AsNoTracking();

        if (shape.HasFlag(RotaReadShape.EventSettings))
            query = query.Include(r => r.EventSettings);

        if (shape.HasFlag(RotaReadShape.ShiftSignups))
            query = query.Include(r => r.Shifts).ThenInclude(s => s.ShiftSignups);
        else if (shape.HasFlag(RotaReadShape.Shifts))
            query = query.Include(r => r.Shifts);

        if (shape.HasFlag(RotaReadShape.Tags))
            query = query.Include(r => r.Tags);

        return await query
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

    public async Task<IReadOnlyList<ConfirmedUserShiftTotal>> GetConfirmedUserShiftTotalsAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? teamIds = null,
        Guid? rotaId = null,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var query = ctx.ShiftSignups
            .AsNoTracking()
            .Where(su => su.Status == SignupStatus.Confirmed
                      && su.Shift.Rota.EventSettingsId == eventSettingsId);

        if (rotaId is { } rid)
            query = query.Where(su => su.Shift.RotaId == rid);
        else if (teamIds is { Count: > 0 })
            query = query.Where(su => teamIds.Contains(su.Shift.Rota.TeamId));

        // Aggregated in memory: NodaTime Duration has no SQL Sum, and one event is a few thousand rows. All-day rows
        // store a 24h sentinel, so they count Shift.AllDayWindowHours instead.
        var rows = await query
            .Select(su => new { su.UserId, su.Shift.Duration, su.Shift.IsAllDay })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.UserId)
            .Select(g => new ConfirmedUserShiftTotal(
                g.Key,
                g.Sum(x => x.IsAllDay ? Shift.AllDayWindowHours : x.Duration.TotalHours),
                g.Count()))
            .ToList();
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

    public async Task<IReadOnlyList<ShiftSignupStatusCount>> GetSignupStatusCountsForEventAsync(
        Guid eventSettingsId,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Only Confirmed and NoShow matter for post-event stats.
        var statusValues = new[] { SignupStatus.Confirmed, SignupStatus.NoShow };

        var rows = await (
            from su in ctx.ShiftSignups.AsNoTracking()
            join sh in ctx.Shifts.AsNoTracking() on su.ShiftId equals sh.Id
            join r in ctx.Rotas.AsNoTracking() on sh.RotaId equals r.Id
            where r.EventSettingsId == eventSettingsId && statusValues.Contains(su.Status)
            select new { su.ShiftId, r.TeamId, sh.DayOffset, su.Status }
        ).ToListAsync(ct);

        return rows
            .GroupBy(x => x.ShiftId)
            .Select(g =>
            {
                var first = g.First();
                return new ShiftSignupStatusCount(
                    ShiftId: first.ShiftId,
                    RotaTeamId: first.TeamId,
                    DayOffset: first.DayOffset,
                    ConfirmedCount: g.Count(x => x.Status == SignupStatus.Confirmed),
                    NoShowCount: g.Count(x => x.Status == SignupStatus.NoShow));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ShiftTag>> GetTagsAsync(string? query = null, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var tags = ctx.ShiftTags.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
            tags = tags.Where(t => EF.Functions.ILike(t.Name, $"%{query}%"));

        return await tags.ToListAsync(ct);
    }

    public async Task<ShiftTag> GetOrCreateTagAsync(string name, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.ShiftTags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => EF.Functions.ILike(t.Name, name), ct);
        if (existing is not null) return existing;

        var tag = new ShiftTag
        {
            Id = Guid.NewGuid(),
            Name = name
        };
        ctx.ShiftTags.Add(tag);
        await ctx.SaveChangesAsync(ct);
        return tag;
    }

    public async Task SetVolunteerTagPreferencesAsync(
        Guid userId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.VolunteerTagPreferences
            .Where(v => v.UserId == userId)
            .ToListAsync(ct);

        ctx.VolunteerTagPreferences.RemoveRange(existing);

        // A duplicate posted id would violate the (UserId, ShiftTagId) unique index.
        foreach (var tagId in tagIds.Distinct())
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

    public async Task ReassignProfilesAndTagPrefsToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // One profile per user (unique index on UserId).
        var sourceProfiles = await ctx.VolunteerEventProfiles
            .Where(p => p.UserId == sourceUserId)
            .ToListAsync(ct);

        var targetHasProfile = await ctx.VolunteerEventProfiles
            .AnyAsync(p => p.UserId == targetUserId, ct);

        foreach (var src in sourceProfiles)
        {
            if (targetHasProfile)
            {
                // Target wins.
                ctx.VolunteerEventProfiles.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                src.UpdatedAt = updatedAt;
                // Any further source row would now collide.
                targetHasProfile = true;
            }
        }

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
                // Target wins.
                ctx.VolunteerTagPreferences.Remove(src);
            }
            else
            {
                src.UserId = targetUserId;
                // The table has no UpdatedAt to stamp.
                targetTagIdSet.Add(src.ShiftTagId);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
