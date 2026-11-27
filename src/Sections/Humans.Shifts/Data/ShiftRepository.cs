using Humans.Base.Data;
using Humans.Shifts.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services.Dtos;
using Humans.Settings.Contracts;
namespace Humans.Shifts.Data;

/// <summary>
/// Management methods use short-lived contexts from the factory; signup mutations use the scoped
/// <see cref="ShiftsDbContext"/> so load/mutate/save flows share one change tracker.
/// </summary>
internal sealed class ShiftRepository : SectionRepository<ShiftsDbContext>, IShiftManagementRepository
{
    private readonly ShiftsDbContext _dbContext;
    private readonly IClock _clock;

    public ShiftRepository(
        IDbContextFactory<ShiftsDbContext> factory,
        ShiftsDbContext dbContext,
        IClock clock)
        : base(factory)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.EventSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct), ct);

    public async Task<IReadOnlyList<EventSettings>> GetAllEventSettingsAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.EventSettings.AsNoTracking().ToListAsync(ct), ct);

    public async Task SaveEventSettingsAsync(EventSettings entity, EntityMutationMode mode, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        if (mode == EntityMutationMode.Add)
            ctx.EventSettings.Add(entity);
        else
            ctx.EventSettings.Update(entity);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveRotaAsync(
        Rota rota, EntityMutationMode mode, CancellationToken ct = default, IReadOnlyList<Guid>? tagIds = null)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        Rota tracked;
        if (mode == EntityMutationMode.Add)
        {
            tracked = rota;
            ctx.Rotas.Add(tracked);
        }
        else
        {
            tracked = await ctx.Rotas.FirstAsync(r => r.Id == rota.Id, ct);
            ctx.Entry(tracked).CurrentValues.SetValues(rota);
            if (tagIds is not null)
                await ctx.Entry(tracked).Collection(r => r.Tags).LoadAsync(ct);
        }

        if (tagIds is not null)
        {
            var tags = await ctx.ShiftTags.Where(t => tagIds.Contains(t.Id)).ToListAsync(ct);
            tracked.Tags.Clear();
            foreach (var tag in tags)
                tracked.Tags.Add(tag);
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateRotaTeamAssignmentAsync(
        Guid rotaId, Guid newTeamId, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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

    public async Task<IReadOnlyList<Rota>> GetRotaRowsAsync(
        IReadOnlyCollection<Guid>? rotaIds, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var query = ctx.Rotas.AsNoTracking()
            .Include(r => r.Tags)
            .Include(r => r.Shifts).ThenInclude(s => s.ShiftSignups)
            .AsQueryable();
        if (rotaIds is not null)
            query = query.Where(r => rotaIds.Contains(r.Id));
        return await query.ToListAsync(ct);
    }

    public async Task DeleteRotaCascadeAsync(Guid rotaId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        string query, Guid eventSettingsId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var pattern = "%" + EscapeLikePattern(query.Trim()) + "%";

        await using var ctx = await CreateDbContextAsync(ct);
        var q = ctx.Rotas
            .AsNoTracking()
            .Where(r => r.EventSettingsId == eventSettingsId
                && r.IsVisibleToVolunteers
                && EF.Functions.ILike(r.Name, pattern, "\\"));

        return await q.ToListAsync(ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public async Task SaveShiftAsync(Shift shift, EntityMutationMode mode, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        if (mode == EntityMutationMode.Add)
            ctx.Shifts.Add(shift);
        else
            ctx.Entry(shift).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddShiftsAsync(IEnumerable<Shift> shifts, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.Shifts.AddRange(shifts);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<Shift?> GetShiftAsync(Guid shiftId, ShiftReadShape shape, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
        var shift = await ctx.Shifts
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct);

        if (shift is null) return;

        ctx.ShiftSignups.RemoveRange(shift.ShiftSignups);
        ctx.Shifts.Remove(shift);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<int>> GetShiftDayOffsetsForRotaAsync(Guid rotaId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Shifts
            .AsNoTracking()
            .Where(s => s.RotaId == rotaId)
            .Select(s => s.DayOffset)
            .Distinct()
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<Shift>> GetEventShiftsAsync(
        ShiftEventQuery request,
        CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

        IQueryable<Shift> query = ctx.Shifts
            .AsNoTracking()
            .Include(s => s.Rota);

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

        await using var ctx = await CreateDbContextAsync(ct);
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

    public async Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByShiftAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default)
    {
        if (shiftIds.Count == 0)
            return new Dictionary<Guid, int>();

        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.ShiftSignups
            .AsNoTracking()
            .Where(su => shiftIds.Contains(su.ShiftId) && su.Status == SignupStatus.Confirmed)
            .GroupBy(su => su.ShiftId)
            .Select(g => new { ShiftId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ShiftId, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<ShiftTag>> GetTagsAsync(string? query = null, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var tags = await ctx.ShiftTags.AsNoTracking().ToListAsync(ct);
        return string.IsNullOrWhiteSpace(query)
            ? tags
            : tags.Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<ShiftTag> GetOrCreateTagAsync(string name, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var tags = await ctx.ShiftTags.AsNoTracking().ToListAsync(ct);
        var existing = tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
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
        await using var ctx = await CreateDbContextAsync(ct);

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
        Guid userId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.VolunteerEventProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct), ct);

    public async Task<IReadOnlyList<VolunteerEventProfile>> GetVolunteerProfilesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.VolunteerEventProfiles
            .AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync(ct);
    }

    public async Task AddVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default) =>
        await InsertAsync(profile, ct);

    public async Task UpdateVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.VolunteerEventProfiles.Update(profile);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteVolunteerEventProfilesForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var profiles = await ctx.VolunteerEventProfiles
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);

        if (profiles.Count == 0)
            return 0;

        ctx.VolunteerEventProfiles.RemoveRange(profiles);
        await ctx.SaveChangesAsync(ct);
        return profiles.Count;
    }

    public async Task ReassignProfilesToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

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

    public async Task<(int Count, IReadOnlyList<Guid> EventIds)> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default)
    {
        var affected = await _dbContext.ShiftSignups
            .Include(s => s.Shift).ThenInclude(s => s.Rota)
            .Where(s => s.UserId == sourceUserId || s.EnrolledByUserId == sourceUserId
                || s.ReviewedByUserId == sourceUserId)
            .ToListAsync(ct);
        var eventIds = affected.Select(s => s.Shift.Rota.EventSettingsId).Distinct().ToList();

        var targetShiftIds = await _dbContext.ShiftSignups
            .Where(s => s.UserId == targetUserId)
            .Select(s => s.ShiftId)
            .ToListAsync(ct);
        var targetShiftIdSet = new HashSet<Guid>(targetShiftIds);

        foreach (var signup in affected)
        {
            if (signup.UserId == sourceUserId)
            {
                if (targetShiftIdSet.Contains(signup.ShiftId))
                {
                    // The target already holds this shift; its slot stands.
                    _dbContext.ShiftSignups.Remove(signup);
                    continue;
                }
                signup.UserId = targetUserId;
            }
            if (signup.EnrolledByUserId == sourceUserId)
                signup.EnrolledByUserId = targetUserId;
            if (signup.ReviewedByUserId == sourceUserId)
                signup.ReviewedByUserId = targetUserId;
            signup.UpdatedAt = updatedAt;
        }

        await _dbContext.SaveChangesAsync(ct);

        return (affected.Count, eventIds);
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

        var zone = settings.TimeZone;
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
