using Humans.Application.DTOs;
using Humans.Application.Enums;
using Humans.Application.Interfaces;
using Humans.Application;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Consolidated service for shift management: authorization, event settings,
/// rotas, shifts, and urgency scoring.
/// </summary>
public class ShiftManagementService : IShiftManagementService
{
    private static readonly TimeSpan AuthCacheDuration = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<ShiftPriority, double> PriorityWeights = new()
    {
        [ShiftPriority.Normal] = 1,
        [ShiftPriority.Important] = 3,
        [ShiftPriority.Essential] = 6
    };

    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<ShiftManagementService> _logger;

    // Lazy-resolved to break circular dependency: TeamService → IShiftManagementService → ITeamService
    private ITeamService TeamService => _serviceProvider.GetRequiredService<ITeamService>();

    public ShiftManagementService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IRoleAssignmentService roleAssignmentService,
        IServiceProvider serviceProvider,
        IMemoryCache cache,
        IClock clock,
        ILogger<ShiftManagementService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _roleAssignmentService = roleAssignmentService;
        _serviceProvider = serviceProvider;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    // ============================================================
    // Authorization
    // ============================================================

    public async Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId)
    {
        var teamIds = await GetCoordinatorTeamIdsAsync(userId);
        if (teamIds.Contains(departmentTeamId))
            return true;

        // Parent department coordinators can manage child teams
        var team = await TeamService.GetTeamByIdAsync(departmentTeamId);
        return team?.ParentTeamId is not null && teamIds.Contains(team.ParentTeamId.Value);
    }

    public async Task<bool> CanManageShiftsAsync(Guid userId, Guid departmentTeamId)
    {
        // Admin and VolunteerCoordinator can manage all shifts system-wide; NoInfoAdmin CANNOT
        if (await HasActiveRoleAsync(userId, RoleNames.Admin) ||
            await HasActiveRoleAsync(userId, RoleNames.VolunteerCoordinator))
            return true;

        return await IsDeptCoordinatorAsync(userId, departmentTeamId);
    }

    public async Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId)
    {
        // Admin, NoInfoAdmin, and VolunteerCoordinator can approve signups system-wide
        if (await HasActiveRoleAsync(userId, RoleNames.Admin) ||
            await HasActiveRoleAsync(userId, RoleNames.NoInfoAdmin) ||
            await HasActiveRoleAsync(userId, RoleNames.VolunteerCoordinator))
            return true;

        return await IsDeptCoordinatorAsync(userId, departmentTeamId);
    }

    public async Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId)
    {
        var result = await _cache.GetOrCreateAsync(CacheKeys.ShiftAuthorization(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = AuthCacheDuration;
            return await QueryCoordinatorTeamIdsAsync(userId);
        });
        return result ?? [];
    }

    private async Task<IReadOnlyList<Guid>> QueryCoordinatorTeamIdsAsync(Guid userId)
    {
        return await TeamService.GetUserCoordinatedTeamIdsAsync(userId);
    }

    private async Task<bool> HasActiveRoleAsync(Guid userId, string roleName)
    {
        return await _roleAssignmentService.HasActiveRoleAsync(userId, roleName);
    }

    // ============================================================
    // EventSettings
    // ============================================================

    public async Task<EventSettings?> GetActiveAsync()
    {
        return await _dbContext.EventSettings
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .FirstOrDefaultAsync(e => e.IsActive);
    }

    public async Task<EventSettings?> GetByIdAsync(Guid id)
    {
        return await _dbContext.EventSettings.FindAsync(id);
    }

    public async Task CreateAsync(EventSettings entity)
    {
        if (entity.IsActive)
        {
            var existing = await _dbContext.EventSettings
                .AnyAsync(e => e.IsActive);
            if (existing)
                throw new InvalidOperationException("Only one EventSettings can be active at a time.");
        }

        entity.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.EventSettings.Add(entity);
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(EventSettings entity)
    {
        if (entity.IsActive)
        {
            var existing = await _dbContext.EventSettings
                .AnyAsync(e => e.IsActive && e.Id != entity.Id);
            if (existing)
                throw new InvalidOperationException("Only one EventSettings can be active at a time.");
        }

        entity.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.EventSettings.Update(entity);
        await _dbContext.SaveChangesAsync();
    }

    public int GetAvailableEeSlots(EventSettings settings, int dayOffset)
    {
        var totalCapacity = settings.GetEarlyEntryCapacityForDay(dayOffset);
        if (totalCapacity == 0) return 0;

        var barriosAllocation = 0;
        if (settings.BarriosEarlyEntryAllocation is not null)
        {
            var applicableKey = int.MinValue;
            foreach (var key in settings.BarriosEarlyEntryAllocation.Keys)
            {
                if (key <= dayOffset && key > applicableKey)
                    applicableKey = key;
            }
            if (applicableKey != int.MinValue)
                barriosAllocation = settings.BarriosEarlyEntryAllocation[applicableKey];
        }

        return Math.Max(0, totalCapacity - barriosAllocation);
    }

    // ============================================================
    // Rota
    // ============================================================

    public async Task CreateRotaAsync(Rota rota)
    {
        var team = await TeamService.GetTeamByIdAsync(rota.TeamId);

        if (team is null)
            throw new InvalidOperationException("Team not found.");
        if (team.SystemTeamType != SystemTeamType.None)
            throw new InvalidOperationException("Rotas cannot be created on system teams.");

        var eventSettings = await _dbContext.EventSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == rota.EventSettingsId && e.IsActive);
        if (eventSettings is null)
            throw new InvalidOperationException("Active EventSettings not found.");

        rota.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.Rotas.Add(rota);
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateRotaAsync(Rota rota)
    {
        rota.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.Rotas.Update(rota);
        await _dbContext.SaveChangesAsync();
    }

    public async Task MoveRotaToTeamAsync(Guid rotaId, Guid targetTeamId, Guid actorUserId)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == rotaId);
        if (rota is null)
            throw new InvalidOperationException("Rota not found.");

        var targetTeam = await TeamService.GetTeamByIdAsync(targetTeamId);
        if (targetTeam is null)
            throw new InvalidOperationException("Target team not found.");
        if (targetTeam.ParentTeamId is not null)
            throw new InvalidOperationException("Rotas can only be moved to parent teams (departments).");
        if (targetTeam.SystemTeamType != SystemTeamType.None)
            throw new InvalidOperationException("Rotas cannot be moved to system teams.");
        if (rota.TeamId == targetTeamId)
            throw new InvalidOperationException("Rota is already in this team.");

        var oldTeamName = rota.Team.Name;
        rota.TeamId = targetTeamId;
        rota.UpdatedAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.RotaMovedToTeam, nameof(Rota), rota.Id,
            $"Moved rota '{rota.Name}' from '{oldTeamName}' to '{targetTeam.Name}'",
            actorUserId,
            relatedEntityId: targetTeamId, relatedEntityType: nameof(Team));

        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteRotaAsync(Guid rotaId)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
            .FirstOrDefaultAsync(r => r.Id == rotaId);

        if (rota is null) throw new InvalidOperationException("Rota not found.");

        var confirmedCount = rota.Shifts
            .SelectMany(s => s.ShiftSignups)
            .Count(d => d.Status == SignupStatus.Confirmed);

        if (confirmedCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete — {confirmedCount} humans have confirmed signups. Bail or reassign them first.");

        // Cancel pending signups and remove all signups before deleting
        // (ShiftSignup→Shift FK is Restrict, so cascade won't handle them)
        foreach (var shift in rota.Shifts)
        {
            foreach (var signup in shift.ShiftSignups.Where(d => d.Status == SignupStatus.Pending).ToList())
            {
                signup.Cancel(_clock, "Rota deleted");
            }
            _dbContext.ShiftSignups.RemoveRange(shift.ShiftSignups);
        }

        _dbContext.Rotas.Remove(rota);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<Rota?> GetRotaByIdAsync(Guid rotaId)
    {
        return await _dbContext.Rotas
            .Include(r => r.Shifts)
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == rotaId);
    }

    public async Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId)
    {
        return await _dbContext.Rotas
            .Include(r => r.EventSettings)
            .Include(r => r.Tags)
            .Include(r => r.Shifts)
                .ThenInclude(s => s.ShiftSignups)
                    .ThenInclude(su => su.User)
            .Where(r => r.TeamId == teamId && r.EventSettingsId == eventSettingsId)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    // ============================================================
    // Bulk Shift Creation
    // ============================================================

    public async Task CreateBuildStrikeShiftsAsync(Guid rotaId, Dictionary<int, (int Min, int Max)> dailyStaffing)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.EventSettings)
            .FirstOrDefaultAsync(r => r.Id == rotaId);

        if (rota is null) throw new InvalidOperationException("Rota not found");
        if (rota.Period == RotaPeriod.Event)
            throw new InvalidOperationException("Build/strike shift generation is only for Build or Strike rotas");

        var es = rota.EventSettings;

        foreach (var dayOffset in dailyStaffing.Keys)
        {
            if (rota.Period == RotaPeriod.Build && (dayOffset < es.BuildStartOffset || dayOffset >= 0))
                throw new InvalidOperationException($"Day offset {dayOffset} is outside the build period ({es.BuildStartOffset} to -1)");
            if (rota.Period == RotaPeriod.Strike && (dayOffset <= es.EventEndOffset || dayOffset > es.StrikeEndOffset))
                throw new InvalidOperationException($"Day offset {dayOffset} is outside the strike period ({es.EventEndOffset + 1} to {es.StrikeEndOffset})");
        }

        // Skip days that already have shifts (additive mode)
        var existingDayOffsets = await _dbContext.Shifts
            .Where(s => s.RotaId == rotaId)
            .Select(s => s.DayOffset)
            .Distinct()
            .ToListAsync();
        var existingSet = existingDayOffsets.ToHashSet();

        var now = _clock.GetCurrentInstant();

        foreach (var (dayOffset, staffing) in dailyStaffing.Where(d => !existingSet.Contains(d.Key)).OrderBy(d => d.Key))
        {
            var shift = new Shift
            {
                Id = Guid.NewGuid(),
                RotaId = rotaId,
                IsAllDay = true,
                DayOffset = dayOffset,
                StartTime = new LocalTime(0, 0),
                Duration = Duration.FromHours(24),
                MinVolunteers = staffing.Min,
                MaxVolunteers = staffing.Max,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Shifts.Add(shift);
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task GenerateEventShiftsAsync(Guid rotaId, int startDayOffset, int endDayOffset,
        List<(LocalTime StartTime, double DurationHours)> timeSlots, int minVolunteers = 2, int maxVolunteers = 5)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.EventSettings)
            .FirstOrDefaultAsync(r => r.Id == rotaId);

        if (rota is null) throw new InvalidOperationException("Rota not found");
        if (rota.Period != RotaPeriod.Event)
            throw new InvalidOperationException("Event shift generation is only for Event-period rotas");

        var now = _clock.GetCurrentInstant();

        for (var day = startDayOffset; day <= endDayOffset; day++)
        {
            foreach (var (startTime, durationHours) in timeSlots)
            {
                var shift = new Shift
                {
                    Id = Guid.NewGuid(),
                    RotaId = rotaId,
                    IsAllDay = false,
                    DayOffset = day,
                    StartTime = startTime,
                    Duration = Duration.FromHours(durationHours),
                    MinVolunteers = minVolunteers,
                    MaxVolunteers = maxVolunteers,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _dbContext.Shifts.Add(shift);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    // ============================================================
    // Shift
    // ============================================================

    public async Task CreateShiftAsync(Shift shift)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.EventSettings)
            .FirstOrDefaultAsync(r => r.Id == shift.RotaId);

        if (rota is null) throw new InvalidOperationException("Rota not found.");

        var es = rota.EventSettings;
        if (shift.DayOffset < es.BuildStartOffset || shift.DayOffset > es.StrikeEndOffset)
            throw new InvalidOperationException(
                $"DayOffset {shift.DayOffset} is outside the valid range ({es.BuildStartOffset}..{es.StrikeEndOffset}).");

        if (shift.MinVolunteers > shift.MaxVolunteers)
            throw new InvalidOperationException("MinVolunteers cannot exceed MaxVolunteers.");

        shift.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.Shifts.Add(shift);
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateShiftAsync(Shift shift)
    {
        shift.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.Shifts.Update(shift);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteShiftAsync(Guid shiftId)
    {
        var shift = await _dbContext.Shifts
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId);

        if (shift is null) throw new InvalidOperationException("Shift not found.");

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete — {confirmedCount} humans have confirmed signups. Bail or reassign them first.");

        // Cancel pending signups, then remove all signups (confirmed already blocked above)
        foreach (var signup in shift.ShiftSignups.Where(d => d.Status == SignupStatus.Pending))
        {
            signup.Cancel(_clock, "Shift deleted");
        }

        _dbContext.ShiftSignups.RemoveRange(shift.ShiftSignups);
        _dbContext.Shifts.Remove(shift);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<Shift?> GetShiftByIdAsync(Guid shiftId)
    {
        return await _dbContext.Shifts
            .Include(s => s.Rota)
                .ThenInclude(r => r.Team)
            .Include(s => s.Rota)
                .ThenInclude(r => r.EventSettings)
            .Include(s => s.ShiftSignups)
            .FirstOrDefaultAsync(s => s.Id == shiftId);
    }

    public async Task<IReadOnlyList<Shift>> GetShiftsByRotaAsync(Guid rotaId)
    {
        return await _dbContext.Shifts
            .Include(s => s.ShiftSignups)
            .Where(s => s.RotaId == rotaId)
            .OrderBy(s => s.DayOffset)
            .ThenBy(s => s.StartTime)
            .ToListAsync();
    }

    public (Instant Start, Instant End, ShiftPeriod Period) ResolveShiftTimes(Shift shift, EventSettings eventSettings)
    {
        var start = shift.GetAbsoluteStart(eventSettings);
        var end = shift.GetAbsoluteEnd(eventSettings);
        var period = shift.GetShiftPeriod(eventSettings);
        return (start, end, period);
    }

    // ============================================================
    // Urgency
    // ============================================================

    public async Task<IReadOnlyList<UrgentShift>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null, LocalDate? date = null, ShiftPeriod? period = null)
    {
        var es = await _dbContext.EventSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
        if (es is null) return [];

        var query = _dbContext.Shifts
            .AsNoTracking()
            .Include(s => s.Rota).ThenInclude(r => r.Team)
            .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.ShiftSignups)
            .Where(s => s.Rota.EventSettingsId == eventSettingsId);

        if (departmentId.HasValue)
            query = query.Where(s => s.Rota.TeamId == departmentId.Value);

        var eventEndOffset = es.EventEndOffset;
        query = period switch
        {
            ShiftPeriod.Build => query.Where(s => s.DayOffset < 0),
            ShiftPeriod.Event => query.Where(s => s.DayOffset >= 0 && s.DayOffset <= eventEndOffset),
            ShiftPeriod.Strike => query.Where(s => s.DayOffset > eventEndOffset),
            _ => query,
        };

        if (date.HasValue)
        {
            var dayOffset = Period.Between(es.GateOpeningDate, date.Value, PeriodUnits.Days).Days;
            query = query.Where(s => s.DayOffset == dayOffset);
        }

        var shifts = await query.ToListAsync();

        var now = _clock.GetCurrentInstant();
        var urgentShifts = shifts
            .Where(s => s.GetAbsoluteEnd(es) > now)
            .Select(s =>
            {
                var confirmedCount = s.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
                var score = CalculateScore(s, confirmedCount, es);
                var remaining = Math.Max(0, s.MaxVolunteers - confirmedCount);
                return new UrgentShift(s, score, confirmedCount, remaining, s.Rota.Team.Name, []);
            })
            .Where(u => u.UrgencyScore > 0)
            .OrderByDescending(u => u.UrgencyScore)
            .ToList();

        if (limit.HasValue)
            return ApplyPeriodDiverseLimit(urgentShifts, limit.Value, es);

        return urgentShifts;
    }

    public async Task<IReadOnlyList<UrgentShift>> GetBrowseShiftsAsync(
        Guid eventSettingsId, Guid? departmentId = null,
        LocalDate? fromDate = null, LocalDate? toDate = null,
        bool includeAdminOnly = false, bool includeSignups = false,
        bool includeHidden = false)
    {
        var es = await _dbContext.EventSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
        if (es is null) return [];

        IQueryable<Shift> query;
        if (includeSignups)
        {
            query = _dbContext.Shifts
                .Include(s => s.Rota).ThenInclude(r => r.Team)
                .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
                .Include(s => s.Rota).ThenInclude(r => r.Tags)
                .Include(s => s.ShiftSignups).ThenInclude(ss => ss.User);
        }
        else
        {
            query = _dbContext.Shifts
                .Include(s => s.Rota).ThenInclude(r => r.Team)
                .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
                .Include(s => s.Rota).ThenInclude(r => r.Tags)
                .Include(s => s.ShiftSignups);
        }

        query = query.Where(s => s.Rota.EventSettingsId == eventSettingsId);

        if (!includeAdminOnly)
            query = query.Where(s => !s.AdminOnly);

        if (!includeHidden)
            query = query.Where(s => s.Rota.IsVisibleToVolunteers);

        if (departmentId.HasValue)
            query = query.Where(s => s.Rota.TeamId == departmentId.Value);

        if (fromDate.HasValue)
        {
            var fromOffset = Period.Between(es.GateOpeningDate, fromDate.Value, PeriodUnits.Days).Days;
            query = query.Where(s => s.DayOffset >= fromOffset);
        }

        if (toDate.HasValue)
        {
            var toOffset = Period.Between(es.GateOpeningDate, toDate.Value, PeriodUnits.Days).Days;
            query = query.Where(s => s.DayOffset <= toOffset);
        }

        var shifts = await query.ToListAsync();

        return shifts
            .Select(s =>
            {
                var confirmedCount = s.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
                var score = CalculateScore(s, confirmedCount, es);
                var remaining = Math.Max(0, s.MaxVolunteers - confirmedCount);
                var signups = includeSignups
                    ? s.ShiftSignups
                        .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                        .Select(ss => (ss.UserId, DisplayName: ss.User?.DisplayName ?? "", ss.Status,
                            HasProfilePicture: ss.User?.ProfilePictureUrl is not null))
                        .OrderBy(ss => ss.Status == SignupStatus.Confirmed ? 0 : 1)
                        .ThenBy(ss => ss.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [];
                return new UrgentShift(s, score, confirmedCount, remaining, s.Rota.Team.Name, signups);
            })
            .OrderByDescending(u => u.UrgencyScore)
            .ToList();
    }

    public double CalculateScore(Shift shift, int confirmedCount, EventSettings eventSettings)
    {
        var remainingSlots = Math.Max(0, shift.MaxVolunteers - confirmedCount);
        if (remainingSlots == 0) return 0;

        var priorityWeight = PriorityWeights.GetValueOrDefault(shift.Rota?.Priority ?? ShiftPriority.Normal, 1);
        var durationHours = shift.Duration.TotalHours;
        var understaffedMultiplier = confirmedCount < shift.MinVolunteers ? 2 : 1;

        // Time proximity: shifts happening sooner get a significant boost.
        // Formula: 1 + 10 / (1 + daysUntilStart)
        // Today → 11x, tomorrow → 6x, 7 days → 2.25x, 30 days → 1.32x
        var now = _clock.GetCurrentInstant();
        var shiftStart = shift.GetAbsoluteStart(eventSettings);
        var daysUntilStart = Math.Max(0, (shiftStart - now).TotalDays);
        var proximityBoost = 1.0 + (10.0 / (1.0 + daysUntilStart));

        return remainingSlots * priorityWeight * durationHours * understaffedMultiplier * proximityBoost;
    }

    /// <summary>
    /// Selects top-N shifts with period diversity so build shifts don't monopolize the list.
    /// Reserves one slot per non-Build period (Event, Strike) that has eligible shifts,
    /// fills remaining slots from the overall top scorers.
    /// </summary>
    public static List<UrgentShift> ApplyPeriodDiverseLimit(
        List<UrgentShift> rankedShifts, int limit, EventSettings es)
    {
        if (rankedShifts.Count <= limit)
            return rankedShifts;

        // Group by computed period
        var byPeriod = rankedShifts
            .GroupBy(u => u.Shift.GetShiftPeriod(es))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Reserve one slot for each non-Build period that has shifts
        var reserved = new List<UrgentShift>();
        var reservedIds = new HashSet<Guid>();
        foreach (var period in new[] { ShiftPeriod.Event, ShiftPeriod.Strike })
        {
            if (byPeriod.TryGetValue(period, out var periodShifts) && periodShifts.Count > 0)
            {
                // Take the highest-scoring shift from this period
                var best = periodShifts[0]; // already sorted by score desc
                reserved.Add(best);
                reservedIds.Add(best.Shift.Id);
            }
        }

        // If we reserved more slots than the limit allows, just take top N from reserved
        if (reserved.Count >= limit)
            return reserved.OrderByDescending(u => u.UrgencyScore).Take(limit).ToList();

        // Fill remaining slots from the overall ranked list, skipping already-reserved
        var result = new List<UrgentShift>(reserved);
        foreach (var shift in rankedShifts)
        {
            if (result.Count >= limit) break;
            if (!reservedIds.Contains(shift.Shift.Id))
                result.Add(shift);
        }

        return result.OrderByDescending(u => u.UrgencyScore).ToList();
    }

    // ============================================================
    // Staffing & Summary
    // ============================================================

    public async Task<IReadOnlyList<DailyStaffingData>> GetStaffingDataAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null)
    {
        var es = await _dbContext.EventSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
        if (es is null) return [];

        var tz = DateTimeZoneProviders.Tzdb[es.TimeZoneId];

        // Day offsets in the requested period (all if null):
        //   Build [BuildStartOffset..-1], Event [0..EventEndOffset], Strike [EventEndOffset+1..StrikeEndOffset]
        var dayOffsets = new List<int>();
        if (period is null or ShiftPeriod.Build)
            for (var d = es.BuildStartOffset; d < 0; d++) dayOffsets.Add(d);
        if (period is null or ShiftPeriod.Event)
            for (var d = 0; d <= es.EventEndOffset; d++) dayOffsets.Add(d);
        if (period is null or ShiftPeriod.Strike)
            for (var d = es.EventEndOffset + 1; d <= es.StrikeEndOffset; d++) dayOffsets.Add(d);

        if (dayOffsets.Count == 0) return [];

        var query = _dbContext.Shifts
            .AsNoTracking()
            .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
            .Include(s => s.ShiftSignups)
            .Where(s => s.Rota.EventSettingsId == eventSettingsId);

        if (departmentId.HasValue)
            query = query.Where(s => s.Rota.TeamId == departmentId.Value);

        var shifts = await query.ToListAsync();
        var results = new List<DailyStaffingData>();

        foreach (var dayOffset in dayOffsets)
        {
            var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
            var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
            var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
            var periodLabel = dayOffset < 0 ? "Set-up" : dayOffset <= es.EventEndOffset ? "Event" : "Strike";
            var dateLabel = dayDate.DayOfWeek.ToString()[..3] + " " + dayDate.ToString("MMM d", null);

            var overlapping = shifts.Where(s =>
            {
                var start = s.GetAbsoluteStart(es);
                var end = s.GetAbsoluteEnd(es);
                return start < dayEnd && end > dayStart;
            }).ToList();

            var totalSlots = overlapping.Sum(s => s.MaxVolunteers);
            var minSlots = overlapping.Sum(s => s.MinVolunteers);
            var confirmedCount = overlapping
                .SelectMany(s => s.ShiftSignups)
                .Count(su => su.Status == SignupStatus.Confirmed);

            results.Add(new DailyStaffingData(dayOffset, dateLabel, confirmedCount, totalSlots, minSlots, periodLabel));
        }

        return results;
    }

    public async Task<IReadOnlyList<DailyStaffingHours>> GetStaffingHoursAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null)
    {
        var es = await _dbContext.EventSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
        if (es is null) return [];

        var tz = DateTimeZoneProviders.Tzdb[es.TimeZoneId];

        var dayOffsets = new List<int>();
        if (period is null or ShiftPeriod.Build)
            for (var d = es.BuildStartOffset; d < 0; d++) dayOffsets.Add(d);
        if (period is null or ShiftPeriod.Event)
            for (var d = 0; d <= es.EventEndOffset; d++) dayOffsets.Add(d);
        if (period is null or ShiftPeriod.Strike)
            for (var d = es.EventEndOffset + 1; d <= es.StrikeEndOffset; d++) dayOffsets.Add(d);

        if (dayOffsets.Count == 0) return [];

        var query = _dbContext.Shifts
            .AsNoTracking()
            .Include(s => s.Rota)
            .Where(s => s.Rota.EventSettingsId == eventSettingsId);

        if (departmentId.HasValue)
            query = query.Where(s => s.Rota.TeamId == departmentId.Value);

        var shifts = await query.ToListAsync();
        var results = new List<DailyStaffingHours>();

        foreach (var dayOffset in dayOffsets)
        {
            var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
            var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
            var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
            var dateLabel = dayDate.DayOfWeek.ToString()[..3] + " " + dayDate.ToString("MMM d", null);

            var overlapping = shifts.Where(s =>
            {
                var start = s.GetAbsoluteStart(es);
                var end = s.GetAbsoluteEnd(es);
                return start < dayEnd && end > dayStart;
            }).ToList();

            var essentialHours = 0.0;
            var importantHours = 0.0;
            var normalHours = 0.0;

            foreach (var shift in overlapping)
            {
                var hours = shift.IsAllDay ? 8.0 : shift.Duration.TotalHours;
                var totalHours = hours * shift.MaxVolunteers;

                switch (shift.Rota.Priority)
                {
                    case ShiftPriority.Essential:
                        essentialHours += totalHours;
                        break;
                    case ShiftPriority.Important:
                        importantHours += totalHours;
                        break;
                    default:
                        normalHours += totalHours;
                        break;
                }
            }

            results.Add(new DailyStaffingHours(dayOffset, dateLabel, essentialHours, importantHours, normalHours));
        }

        return results;
    }

    public async Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
        Guid eventSettingsId, Guid departmentTeamId)
    {
        var rotas = await _dbContext.Rotas
            .AsNoTracking()
            .Include(r => r.Shifts).ThenInclude(s => s.ShiftSignups)
            .Where(r => r.EventSettingsId == eventSettingsId && r.TeamId == departmentTeamId)
            .ToListAsync();

        if (rotas.Count == 0) return null;

        var allShifts = rotas.SelectMany(r => r.Shifts).ToList();
        if (allShifts.Count == 0) return null;

        var allSignups = allShifts.SelectMany(s => s.ShiftSignups).ToList();

        return new ShiftsSummaryData(
            TotalSlots: allShifts.Sum(s => s.MaxVolunteers),
            ConfirmedCount: allSignups.Count(s => s.Status == SignupStatus.Confirmed),
            PendingCount: allSignups
                .Where(s => s.Status == SignupStatus.Pending)
                .Select(s => s.SignupBlockId ?? s.Id)
                .Distinct()
                .Count(),
            UniqueVolunteerCount: allSignups
                .Where(s => s.Status == SignupStatus.Confirmed)
                .Select(s => s.UserId)
                .Distinct()
                .Count());
    }

    public async Task<ShiftsSummaryData?> GetShiftsSummaryForTeamsAsync(
        Guid eventSettingsId, IReadOnlyList<Guid> teamIds)
    {
        if (teamIds.Count == 0) return null;

        var rotas = await _dbContext.Rotas
            .AsNoTracking()
            .Include(r => r.Shifts).ThenInclude(s => s.ShiftSignups)
            .Where(r => r.EventSettingsId == eventSettingsId && teamIds.Contains(r.TeamId))
            .ToListAsync();

        if (rotas.Count == 0) return null;

        var allShifts = rotas.SelectMany(r => r.Shifts).ToList();
        if (allShifts.Count == 0) return null;

        var allSignups = allShifts.SelectMany(s => s.ShiftSignups).ToList();

        return new ShiftsSummaryData(
            TotalSlots: allShifts.Sum(s => s.MaxVolunteers),
            ConfirmedCount: allSignups.Count(s => s.Status == SignupStatus.Confirmed),
            PendingCount: allSignups
                .Where(s => s.Status == SignupStatus.Pending)
                .Select(s => s.SignupBlockId ?? s.Id)
                .Distinct()
                .Count(),
            UniqueVolunteerCount: allSignups
                .Where(s => s.Status == SignupStatus.Confirmed)
                .Select(s => s.UserId)
                .Distinct()
                .Count());
    }

    public async Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId)
    {
        var teams = await _dbContext.Rotas
            .AsNoTracking()
            .Where(r => r.EventSettingsId == eventSettingsId)
            .Select(r => new { r.Team.Id, r.Team.Name })
            .Distinct()
            .OrderBy(x => x.Name)
            .ToListAsync();

        return teams.Select(x => (x.Id, x.Name)).ToList();
    }

    // ============================================================
    // Coordinator Dashboard
    // ============================================================

    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromMinutes(5);

    internal static string OverviewCacheKey(Guid eventId, ShiftPeriod? period) =>
        $"dashboard-overview:{eventId}:{period?.ToString() ?? "all"}";
    internal static string CoordinatorActivityCacheKey(Guid eventId, ShiftPeriod? period) =>
        $"dashboard-coordinator-activity:{eventId}:{period?.ToString() ?? "all"}";
    internal static string TrendsCacheKey(Guid eventId, TrendWindow window, ShiftPeriod? period) =>
        $"dashboard-trends:{eventId}:{window}:{period?.ToString() ?? "all"}";

    public async Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null)
    {
        var cached = await _cache.GetOrCreateAsync(OverviewCacheKey(eventSettingsId, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await ComputeDashboardOverviewAsync(eventSettingsId, period);
        });
        return cached!;
    }

    private async Task<DashboardOverview> ComputeDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period)
    {
        var es = await _dbContext.EventSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
        if (es is null)
            return EmptyOverview();

        var allShifts = await _dbContext.Shifts.AsNoTracking()
            .Include(s => s.Rota).ThenInclude(r => r.Team).ThenInclude(t => t.ParentTeam)
            .Where(s => s.Rota.EventSettingsId == eventSettingsId
                        && !s.AdminOnly
                        && s.Rota.IsVisibleToVolunteers)
            .ToListAsync();

        var shifts = period is null
            ? allShifts
            : allShifts.Where(s => s.GetShiftPeriod(es) == period.Value).ToList();

        var shiftIds = shifts.Select(s => s.Id).ToList();

        var confirmedCounts = await _dbContext.ShiftSignups.AsNoTracking()
            .Where(su => shiftIds.Contains(su.ShiftId) && su.Status == SignupStatus.Confirmed)
            .GroupBy(su => su.ShiftId)
            .Select(g => new { ShiftId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ShiftId, x => x.Count);

        int ConfirmedOn(Guid shiftId) => confirmedCounts.TryGetValue(shiftId, out var c) ? c : 0;
        bool IsFilled(Shift s) => ConfirmedOn(s.Id) >= s.MinVolunteers;

        var totalShifts = shifts.Count;
        var filledShifts = shifts.Count(IsFilled);

        PeriodBreakdown periodFillRates;
        {
            var perPeriod = shifts
                .GroupBy(s => s.GetShiftPeriod(es))
                .ToDictionary(g => g.Key, g => (Total: g.Count(), Filled: g.Count(IsFilled)));
            periodFillRates = new PeriodBreakdown(
                Pct(perPeriod, ShiftPeriod.Build),
                Pct(perPeriod, ShiftPeriod.Event),
                Pct(perPeriod, ShiftPeriod.Strike));
        }

        var ticketHolderIds = await _dbContext.TicketOrders.AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId != null)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();
        var ticketHolders = ticketHolderIds.ToHashSet();

        var engagedUserIds = await _dbContext.ShiftSignups.AsNoTracking()
            .Where(su => shiftIds.Contains(su.ShiftId) && su.Status != SignupStatus.Cancelled)
            .Select(su => su.UserId)
            .Distinct()
            .ToListAsync();
        var engaged = engagedUserIds.ToHashSet();

        var ticketHoldersEngaged = engaged.Count(u => ticketHolders.Contains(u));
        var nonTicketSignups = engaged.Count(u => !ticketHolders.Contains(u));

        var staleThreshold = _clock.GetCurrentInstant().Minus(Duration.FromDays(3));
        var stalePendingCount = await _dbContext.ShiftSignups.AsNoTracking()
            .CountAsync(su => shiftIds.Contains(su.ShiftId)
                              && su.Status == SignupStatus.Pending
                              && su.CreatedAt < staleThreshold);

        var departments = BuildDepartmentRows(shifts, confirmedCounts, es);

        return new DashboardOverview(
            totalShifts, filledShifts, periodFillRates,
            ticketHolders.Count, ticketHoldersEngaged, nonTicketSignups, stalePendingCount,
            departments);

        static double Pct(Dictionary<ShiftPeriod, (int Total, int Filled)> perPeriod, ShiftPeriod p) =>
            perPeriod.TryGetValue(p, out var v) && v.Total > 0
                ? 100.0 * v.Filled / v.Total
                : 0d;
    }

    private static DashboardOverview EmptyOverview() => new(
        0, 0, new PeriodBreakdown(0, 0, 0), 0, 0, 0, 0, Array.Empty<DepartmentStaffingRow>());

    private static List<DepartmentStaffingRow> BuildDepartmentRows(
        List<Shift> shifts,
        Dictionary<Guid, int> confirmedCounts,
        EventSettings es)
    {
        // Helper: resolve department (parent team if any, else own team) from a shift.
        static Team DeptOf(Shift s) => s.Rota.Team.ParentTeam ?? s.Rota.Team;

        // Group by department ID (Team instances may differ across includes; grouping by identity is incorrect).
        var groups = shifts
            .GroupBy(s => DeptOf(s).Id)
            .ToList();

        var rows = new List<DepartmentStaffingRow>();
        foreach (var g in groups)
        {
            var deptShifts = g.ToList();
            var dept = DeptOf(deptShifts[0]);
            var agg = AggregateShifts(deptShifts, confirmedCounts, es);

            // Subgroups: any shift belongs to a subteam?
            var subgroups = new List<SubgroupStaffingRow>();
            var anySubteam = deptShifts.Any(s => s.Rota.Team.ParentTeamId != null);
            if (anySubteam)
            {
                // Per-subteam subgroups (group by team ID for stable identity across EF-include instances).
                var subteamGroups = deptShifts
                    .Where(s => s.Rota.Team.ParentTeamId != null)
                    .GroupBy(s => s.Rota.Team.Id);
                foreach (var sg in subteamGroups)
                {
                    var sTeam = sg.First().Rota.Team;
                    var sAgg = AggregateShifts(sg.ToList(), confirmedCounts, es);
                    subgroups.Add(new SubgroupStaffingRow(
                        sTeam.Id, sTeam.Name, IsDirect: false,
                        sAgg.Total, sAgg.Filled, sAgg.Remaining,
                        sAgg.Build, sAgg.Event, sAgg.Strike));
                }

                // "Direct" row for department's own rotas (if any).
                var directShifts = deptShifts.Where(s => s.Rota.TeamId == dept.Id).ToList();
                if (directShifts.Count > 0)
                {
                    var dAgg = AggregateShifts(directShifts, confirmedCounts, es);
                    subgroups.Insert(0, new SubgroupStaffingRow(
                        dept.Id, "Direct", IsDirect: true,
                        dAgg.Total, dAgg.Filled, dAgg.Remaining,
                        dAgg.Build, dAgg.Event, dAgg.Strike));
                }

                // Sort: Direct pinned top; rest by fill % ascending, ties by name.
                subgroups = subgroups
                    .OrderByDescending(r => r.IsDirect)
                    .ThenBy(r => r.TotalShifts == 0 ? 0 : 100.0 * r.FilledShifts / r.TotalShifts)
                    .ThenBy(r => r.Name, StringComparer.Ordinal)
                    .ToList();
            }

            rows.Add(new DepartmentStaffingRow(
                dept.Id, dept.Name,
                agg.Total, agg.Filled, agg.Remaining,
                agg.Build, agg.Event, agg.Strike,
                subgroups));
        }

        return rows
            .OrderBy(r => r.TotalShifts == 0 ? 0 : 100.0 * r.FilledShifts / r.TotalShifts)
            .ThenBy(r => r.DepartmentName, StringComparer.Ordinal)
            .ToList();
    }

    private static (int Total, int Filled, int Remaining, PeriodStaffing Build, PeriodStaffing Event, PeriodStaffing Strike)
        AggregateShifts(List<Shift> shifts, Dictionary<Guid, int> confirmedCounts, EventSettings es)
    {
        int total = shifts.Count;
        int filled = 0;
        int remaining = 0;
        var periodAgg = new Dictionary<ShiftPeriod, (int Total, int Filled, int Remaining)>
        {
            [ShiftPeriod.Build] = (0, 0, 0),
            [ShiftPeriod.Event] = (0, 0, 0),
            [ShiftPeriod.Strike] = (0, 0, 0),
        };

        foreach (var s in shifts)
        {
            var confirmed = confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0;
            var isFilled = confirmed >= s.MinVolunteers;
            var slotsLeft = Math.Max(0, s.MaxVolunteers - confirmed);

            if (isFilled) filled++;
            remaining += slotsLeft;

            var p = s.GetShiftPeriod(es);
            var cur = periodAgg[p];
            periodAgg[p] = (cur.Total + 1, cur.Filled + (isFilled ? 1 : 0), cur.Remaining + slotsLeft);
        }

        PeriodStaffing ToStaffing(ShiftPeriod p)
        {
            var v = periodAgg[p];
            return new PeriodStaffing(v.Total, v.Filled, v.Remaining);
        }

        return (total, filled, remaining, ToStaffing(ShiftPeriod.Build), ToStaffing(ShiftPeriod.Event), ToStaffing(ShiftPeriod.Strike));
    }

    public async Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period = null)
    {
        var cached = await _cache.GetOrCreateAsync(CoordinatorActivityCacheKey(eventSettingsId, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await ComputeCoordinatorActivityAsync(eventSettingsId, period);
        });
        return cached!;
    }

    private async Task<IReadOnlyList<CoordinatorActivityRow>> ComputeCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period)
    {
        // Need EventEndOffset for period filtering via DayOffset at the DB level.
        int eventEndOffset = 0;
        if (period is not null)
        {
            var es = await _dbContext.EventSettings.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
            if (es is null) return Array.Empty<CoordinatorActivityRow>();
            eventEndOffset = es.EventEndOffset;
        }

        // Pending signup counts per team (via rota.TeamId). Not deduped by SignupBlockId —
        // this count is the raw number of pending shift-signups that need attention.
        var pendingQuery =
            from rota in _dbContext.Rotas
            where rota.EventSettingsId == eventSettingsId
            join shift in _dbContext.Shifts on rota.Id equals shift.RotaId
            join signup in _dbContext.ShiftSignups on shift.Id equals signup.ShiftId
            where signup.Status == SignupStatus.Pending
            select new { rota.TeamId, shift.DayOffset };

        pendingQuery = period switch
        {
            ShiftPeriod.Build => pendingQuery.Where(x => x.DayOffset < 0),
            ShiftPeriod.Event => pendingQuery.Where(x => x.DayOffset >= 0 && x.DayOffset <= eventEndOffset),
            ShiftPeriod.Strike => pendingQuery.Where(x => x.DayOffset > eventEndOffset),
            _ => pendingQuery,
        };

        var pendingCounts = await pendingQuery
            .GroupBy(x => x.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count);

        if (pendingCounts.Count == 0)
            return Array.Empty<CoordinatorActivityRow>();

        // Load the full team hierarchy so we can build the tree and include ancestors
        // of any team that has pending (a parent with no pending of its own but pending
        // subteams still needs to appear as the top-level row).
        var teamMeta = await _dbContext.Teams.AsNoTracking()
            .Select(t => new { t.Id, t.Name, t.ParentTeamId })
            .ToDictionaryAsync(t => t.Id);

        var relevantTeamIds = new HashSet<Guid>(pendingCounts.Keys);
        foreach (var id in pendingCounts.Keys.ToList())
        {
            var current = teamMeta.GetValueOrDefault(id);
            while (current?.ParentTeamId is Guid parentId && teamMeta.ContainsKey(parentId))
            {
                if (!relevantTeamIds.Add(parentId))
                    break;
                current = teamMeta[parentId];
            }
        }

        // Coordinators per relevant team.
        var coordsRaw = await _dbContext.TeamMembers.AsNoTracking()
            .Where(m => relevantTeamIds.Contains(m.TeamId)
                        && m.LeftAt == null
                        && m.Role == TeamMemberRole.Coordinator)
            .Select(m => new { m.TeamId, m.UserId, m.User.DisplayName, m.User.LastLoginAt })
            .ToListAsync();

        var coordsByTeam = coordsRaw
            .GroupBy(c => c.TeamId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CoordinatorLogin>)g
                    .Select(c => new CoordinatorLogin(c.UserId, c.DisplayName ?? string.Empty, c.LastLoginAt))
                    .OrderBy(c => c.LastLoginAt ?? Instant.MinValue)
                    .ToList());

        // Build recursive rows.
        CoordinatorActivityRow BuildRow(Guid teamId)
        {
            var t = teamMeta[teamId];
            var ownPending = pendingCounts.GetValueOrDefault(teamId, 0);
            var coords = coordsByTeam.GetValueOrDefault(teamId, Array.Empty<CoordinatorLogin>());

            var childIds = relevantTeamIds
                .Where(id => teamMeta[id].ParentTeamId == teamId);

            var subgroups = childIds
                .Select(BuildRow)
                .OrderBy(SubtreeOldestLogin)
                .ThenBy(r => r.TeamName, StringComparer.Ordinal)
                .ToList();

            var aggregate = ownPending + subgroups.Sum(s => s.AggregatePendingCount);
            return new CoordinatorActivityRow(teamId, t.Name, coords, ownPending, aggregate, subgroups);
        }

        // Root teams: no parent, or parent not in the relevant set (defensive — shouldn't happen).
        var rootIds = relevantTeamIds
            .Where(id =>
            {
                var t = teamMeta[id];
                return !t.ParentTeamId.HasValue || !relevantTeamIds.Contains(t.ParentTeamId.Value);
            });

        return rootIds
            .Select(BuildRow)
            .Where(r => r.AggregatePendingCount > 0)
            .OrderBy(SubtreeOldestLogin)
            .ThenBy(r => r.TeamName, StringComparer.Ordinal)
            .ToList();

        static Instant SubtreeOldestLogin(CoordinatorActivityRow row)
        {
            var oldest = Instant.MaxValue;
            var found = false;
            void Walk(CoordinatorActivityRow r)
            {
                foreach (var c in r.Coordinators)
                {
                    var lv = c.LastLoginAt ?? Instant.MinValue;
                    if (!found || lv < oldest) { oldest = lv; found = true; }
                }
                foreach (var sub in r.Subgroups) Walk(sub);
            }
            Walk(row);
            return found ? oldest : Instant.MinValue;
        }
    }

    public async Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null)
    {
        var cached = await _cache.GetOrCreateAsync(TrendsCacheKey(eventSettingsId, window, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await ComputeDashboardTrendsAsync(eventSettingsId, window, period);
        });
        return cached!;
    }

    private async Task<IReadOnlyList<DashboardTrendPoint>> ComputeDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period)
    {
        var es = await _dbContext.EventSettings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
        if (es is null) return Array.Empty<DashboardTrendPoint>();

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;
        var now = _clock.GetCurrentInstant();
        var today = now.InZone(tz).Date;

        LocalDate start = window switch
        {
            TrendWindow.Last7Days => today.PlusDays(-6),
            TrendWindow.Last30Days => today.PlusDays(-29),
            TrendWindow.Last90Days => today.PlusDays(-89),
            TrendWindow.All => es.CreatedAt.InZone(tz).Date,
            _ => today.PlusDays(-29),
        };

        if (start > today) start = today;

        var startInstant = start.AtStartOfDayInZone(tz).ToInstant();
        var endInstant = today.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();

        // Signups created in window, scoped to this event's shifts. Period filter applies
        // only to this series — ticket sales and logins are event-wide, not period-specific.
        var eventEndOffset = es.EventEndOffset;
        var signupsQuery =
            from su in _dbContext.ShiftSignups.AsNoTracking()
            join sh in _dbContext.Shifts.AsNoTracking() on su.ShiftId equals sh.Id
            join r in _dbContext.Rotas.AsNoTracking() on sh.RotaId equals r.Id
            where r.EventSettingsId == eventSettingsId
                  && su.CreatedAt >= startInstant
                  && su.CreatedAt < endInstant
            select new { su.CreatedAt, sh.DayOffset };

        signupsQuery = period switch
        {
            ShiftPeriod.Build => signupsQuery.Where(x => x.DayOffset < 0),
            ShiftPeriod.Event => signupsQuery.Where(x => x.DayOffset >= 0 && x.DayOffset <= eventEndOffset),
            ShiftPeriod.Strike => signupsQuery.Where(x => x.DayOffset > eventEndOffset),
            _ => signupsQuery,
        };

        var signupsInWindow = await signupsQuery.Select(x => x.CreatedAt).ToListAsync();

        var ticketsInWindow = await _dbContext.TicketOrders.AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid
                        && o.PurchasedAt >= startInstant
                        && o.PurchasedAt < endInstant)
            .Select(o => o.PurchasedAt)
            .ToListAsync();

        var loginsInWindow = await _dbContext.Users.AsNoTracking()
            .Where(u => u.LastLoginAt != null
                        && u.LastLoginAt >= startInstant
                        && u.LastLoginAt < endInstant)
            .Select(u => u.LastLoginAt!.Value)
            .ToListAsync();

        LocalDate ToLocalDate(Instant i) => i.InZone(tz).Date;

        var signupsByDay = signupsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
        var ticketsByDay = ticketsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
        var loginsByDay = loginsInWindow.Select(ToLocalDate).Distinct().ToHashSet();
        // DistinctLogins as daily count = each user counted at most once on their LastLoginAt day; that's what we have.
        var loginCountsByDay = loginsInWindow
            .GroupBy(i => ToLocalDate(i))
            .ToDictionary(g => g.Key, g => g.Count());

        var points = new List<DashboardTrendPoint>();
        for (var d = start; d <= today; d = d.PlusDays(1))
        {
            points.Add(new DashboardTrendPoint(
                d,
                signupsByDay.TryGetValue(d, out var s) ? s : 0,
                ticketsByDay.TryGetValue(d, out var t) ? t : 0,
                loginCountsByDay.TryGetValue(d, out var l) ? l : 0));
        }
        return points;
    }

    // ============================================================
    // Shift Tags
    // ============================================================

    public async Task<IReadOnlyList<ShiftTag>> GetAllTagsAsync()
    {
        return await _dbContext.ShiftTags
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ShiftTag>> SearchTagsAsync(string query)
    {
        return await _dbContext.ShiftTags
            .AsNoTracking()
            .Where(t => EF.Functions.ILike(t.Name, $"%{query}%"))
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<ShiftTag> GetOrCreateTagAsync(string name)
    {
        var trimmed = name.Trim();
        var existing = await _dbContext.ShiftTags
            .FirstOrDefaultAsync(t => EF.Functions.ILike(t.Name, trimmed));

        if (existing is not null)
            return existing;

        var tag = new ShiftTag
        {
            Id = Guid.NewGuid(),
            Name = trimmed
        };
        _dbContext.ShiftTags.Add(tag);
        await _dbContext.SaveChangesAsync();
        return tag;
    }

    public async Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds)
    {
        var rota = await _dbContext.Rotas
            .Include(r => r.Tags)
            .FirstOrDefaultAsync(r => r.Id == rotaId);

        if (rota is null) return;

        rota.Tags.Clear();

        if (tagIds.Count > 0)
        {
            var tags = await _dbContext.ShiftTags
                .Where(t => tagIds.Contains(t.Id))
                .ToListAsync();

            foreach (var tag in tags)
            {
                rota.Tags.Add(tag);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ShiftTag>> GetVolunteerTagPreferencesAsync(Guid userId)
    {
        return await _dbContext.VolunteerTagPreferences
            .AsNoTracking()
            .Where(v => v.UserId == userId)
            .Select(v => v.ShiftTag)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds)
    {
        var existing = await _dbContext.VolunteerTagPreferences
            .Where(v => v.UserId == userId)
            .ToListAsync();

        _dbContext.VolunteerTagPreferences.RemoveRange(existing);

        foreach (var tagId in tagIds)
        {
            _dbContext.VolunteerTagPreferences.Add(new VolunteerTagPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftTagId = tagId
            });
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetPendingShiftSignupCountsByTeamAsync(
        Guid eventSettingsId,
        CancellationToken cancellationToken = default)
    {
        var activeEventId = await _dbContext.EventSettings
            .Where(e => e.IsActive && e.Id == eventSettingsId)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeEventId == Guid.Empty)
        {
            return new Dictionary<Guid, int>();
        }

        return await (
            from rota in _dbContext.Rotas
            where rota.EventSettingsId == activeEventId
            join shift in _dbContext.Shifts on rota.Id equals shift.RotaId
            join signup in _dbContext.ShiftSignups on shift.Id equals signup.ShiftId
            where signup.Status == SignupStatus.Pending
            select new { rota.TeamId, BlockKey = signup.SignupBlockId ?? signup.Id }
        )
        .Distinct()
        .GroupBy(x => x.TeamId)
        .Select(g => new { TeamId = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.TeamId, x => x.Count, cancellationToken);
    }

    // ============================================================
    // Volunteer Event Profiles
    // ============================================================

    public async Task<VolunteerEventProfile> GetOrCreateShiftProfileAsync(Guid userId)
    {
        var existing = await _dbContext.VolunteerEventProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (existing is not null)
            return existing;

        var now = _clock.GetCurrentInstant();
        var profile = new VolunteerEventProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.VolunteerEventProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        return profile;
    }

    public async Task UpdateShiftProfileAsync(VolunteerEventProfile profile)
    {
        profile.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.VolunteerEventProfiles.Update(profile);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<VolunteerEventProfile?> GetShiftProfileAsync(Guid userId, bool includeMedical)
    {
        var profile = await _dbContext.VolunteerEventProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile is not null && !includeMedical)
        {
            profile.MedicalConditions = null;
        }

        return profile;
    }
}
