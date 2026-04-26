using Humans.Application.DTOs;
using Humans.Application.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Consolidated shift-management service (authorization, event settings,
/// rotas, shifts, urgency scoring, coordinator dashboard, shift tags,
/// volunteer event profiles). Migrated to <c>Humans.Application</c> per
/// §15 in issue #541a — never imports <c>Microsoft.EntityFrameworkCore</c>.
///
/// <para>
/// Caching strategy: the 60-second <c>IMemoryCache</c> entry for
/// coordinator-team-ids (<c>shift-auth:{userId}</c>) is kept — it sits on a
/// very hot path and the short TTL is intentional (see design-rules §15f).
/// The 5-minute dashboard cache (overview / coordinator-activity / trends)
/// is kept for the same reason. Per §15 we do NOT cache full shift-grid
/// projections — those reads go straight through the repository.
/// </para>
///
/// <para>
/// Cross-section reads go through <c>ITeamService</c>, <c>IUserService</c>,
/// and <c>ITicketQueryService</c>. No <c>.Include()</c> crosses a domain
/// boundary; team metadata and signup user info are stitched in memory
/// (design-rules §6b).
/// </para>
/// </summary>
public sealed class ShiftManagementService : IShiftManagementService, IShiftAuthorizationInvalidator
{
    private static readonly TimeSpan AuthCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<ShiftPriority, double> PriorityWeights = new()
    {
        [ShiftPriority.Normal] = 1,
        [ShiftPriority.Important] = 3,
        [ShiftPriority.Essential] = 6
    };

    private readonly IShiftManagementRepository _repo;
    private readonly IAuditLogService _auditLogService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<ShiftManagementService> _logger;

    // Lazy-resolved to break circular dependency: TeamService → IShiftManagementService → ITeamService
    private ITeamService TeamService => _serviceProvider.GetRequiredService<ITeamService>();

    // Lazy-resolved to break the constructor-time cycle:
    // IUserService -> TeamService -> IShiftManagementService -> IRoleAssignmentService -> IUserService.
    private IRoleAssignmentService RoleAssignmentService => _serviceProvider.GetRequiredService<IRoleAssignmentService>();

    // Resolved on demand so this class stays construction-cheap in tests and
    // avoids forcing the (heavy) ticket/user services as hard ctor dependencies.
    // These are only used by the coordinator dashboard compute methods, which
    // already live behind a 5-minute sliding cache.
    private ITicketQueryService TicketQueryService => _serviceProvider.GetRequiredService<ITicketQueryService>();
    private IUserService UserService => _serviceProvider.GetRequiredService<IUserService>();

    public ShiftManagementService(
        IShiftManagementRepository repo,
        IAuditLogService auditLogService,
        IServiceProvider serviceProvider,
        IMemoryCache cache,
        IClock clock,
        ILogger<ShiftManagementService> logger)
    {
        _repo = repo;
        _auditLogService = auditLogService;
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
            return await TeamService.GetUserCoordinatedTeamIdsAsync(userId);
        });
        return result ?? [];
    }

    private async Task<bool> HasActiveRoleAsync(Guid userId, string roleName)
    {
        return await RoleAssignmentService.HasActiveRoleAsync(userId, roleName);
    }

    /// <summary>
    /// Drops the cached coordinator-team-id list for a single user.
    /// Implements <see cref="IShiftAuthorizationInvalidator"/> so external
    /// sections (Profile deletion, Team coordinator changes) can signal
    /// staleness without owning the cache mechanics.
    /// </summary>
    public void Invalidate(Guid userId)
    {
        _cache.Remove(CacheKeys.ShiftAuthorization(userId));
    }

    // ============================================================
    // EventSettings
    // ============================================================

    public Task<EventSettings?> GetActiveAsync() =>
        _repo.GetActiveEventSettingsAsync();

    public Task<EventSettings?> GetByIdAsync(Guid id) =>
        _repo.GetEventSettingsByIdAsync(id);

    public async Task CreateAsync(EventSettings entity)
    {
        if (entity.IsActive)
        {
            var conflict = await _repo.AnyOtherActiveEventSettingsAsync(excludingId: null);
            if (conflict)
                throw new InvalidOperationException("Only one EventSettings can be active at a time.");
        }

        entity.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.AddEventSettingsAsync(entity);
    }

    public async Task UpdateAsync(EventSettings entity)
    {
        if (entity.IsActive)
        {
            var conflict = await _repo.AnyOtherActiveEventSettingsAsync(excludingId: entity.Id);
            if (conflict)
                throw new InvalidOperationException("Only one EventSettings can be active at a time.");
        }

        entity.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.UpdateEventSettingsAsync(entity);
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

        var eventSettings = await _repo.GetEventSettingsByIdAsync(rota.EventSettingsId);
        if (eventSettings is null || !eventSettings.IsActive)
            throw new InvalidOperationException("Active EventSettings not found.");

        rota.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.AddRotaAsync(rota);
    }

    public async Task UpdateRotaAsync(Rota rota)
    {
        rota.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.UpdateRotaAsync(rota);
    }

    public async Task MoveRotaToTeamAsync(Guid rotaId, Guid targetTeamId, Guid actorUserId)
    {
        var rota = await _repo.GetRotaForUpdateAsync(rotaId);
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

        // Fetch the old team name via ITeamService — no cross-domain Include.
        var oldTeam = await TeamService.GetTeamByIdAsync(rota.TeamId);
        var oldTeamName = oldTeam?.Name ?? "(unknown)";

        // Targeted write: only TeamId + UpdatedAt are marked modified, so a
        // concurrent admin editing unrelated rota fields (Name, Period, …)
        // does not have their save clobbered by this detached-graph update.
        await _repo.UpdateRotaTeamAssignmentAsync(
            rota.Id, targetTeamId, _clock.GetCurrentInstant());

        await _auditLogService.LogAsync(
            AuditAction.RotaMovedToTeam, nameof(Rota), rota.Id,
            $"Moved rota '{rota.Name}' from '{oldTeamName}' to '{targetTeam.Name}'",
            actorUserId,
            relatedEntityId: targetTeamId, relatedEntityType: nameof(Team));
    }

    public async Task DeleteRotaAsync(Guid rotaId)
    {
        var rota = await _repo.GetRotaWithShiftsAndSignupsForDeleteAsync(rotaId);
        if (rota is null) throw new InvalidOperationException("Rota not found.");

        var confirmedCount = rota.Shifts
            .SelectMany(s => s.ShiftSignups)
            .Count(d => d.Status == SignupStatus.Confirmed);

        if (confirmedCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete — {confirmedCount} humans have confirmed signups. Bail or reassign them first.");

        // Cancel pending signups on the tracked entities before cascade delete
        // (ShiftSignup→Shift FK is Restrict; the repo removes signups atomically).
        foreach (var shift in rota.Shifts)
        {
            foreach (var signup in shift.ShiftSignups.Where(d => d.Status == SignupStatus.Pending).ToList())
            {
                signup.Cancel(_clock, "Rota deleted");
            }
        }

        await _repo.DeleteRotaCascadeAsync(rotaId);
    }

    public Task<Rota?> GetRotaByIdAsync(Guid rotaId) =>
        _repo.GetRotaByIdWithShiftsAsync(rotaId);

    public Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId) =>
        _repo.GetRotasByDepartmentAsync(teamId, eventSettingsId);

    // ============================================================
    // Bulk Shift Creation
    // ============================================================

    public async Task CreateBuildStrikeShiftsAsync(Guid rotaId, Dictionary<int, (int Min, int Max)> dailyStaffing)
    {
        var rota = await _repo.GetRotaWithEventSettingsAsync(rotaId);
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
        var existingDayOffsets = await _repo.GetShiftDayOffsetsForRotaAsync(rotaId);
        var existingSet = existingDayOffsets.ToHashSet();

        var now = _clock.GetCurrentInstant();
        var toInsert = new List<Shift>();

        foreach (var (dayOffset, staffing) in dailyStaffing.Where(d => !existingSet.Contains(d.Key)).OrderBy(d => d.Key))
        {
            toInsert.Add(new Shift
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
            });
        }

        if (toInsert.Count > 0)
            await _repo.AddShiftsAsync(toInsert);
    }

    public async Task GenerateEventShiftsAsync(Guid rotaId, int startDayOffset, int endDayOffset,
        List<(LocalTime StartTime, double DurationHours)> timeSlots, int minVolunteers = 2, int maxVolunteers = 5)
    {
        var rota = await _repo.GetRotaWithEventSettingsAsync(rotaId);
        if (rota is null) throw new InvalidOperationException("Rota not found");
        if (rota.Period != RotaPeriod.Event)
            throw new InvalidOperationException("Event shift generation is only for Event-period rotas");

        var now = _clock.GetCurrentInstant();
        var toInsert = new List<Shift>();

        for (var day = startDayOffset; day <= endDayOffset; day++)
        {
            foreach (var (startTime, durationHours) in timeSlots)
            {
                toInsert.Add(new Shift
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
                });
            }
        }

        if (toInsert.Count > 0)
            await _repo.AddShiftsAsync(toInsert);
    }

    // ============================================================
    // Shift
    // ============================================================

    public async Task CreateShiftAsync(Shift shift)
    {
        var rota = await _repo.GetRotaWithEventSettingsAsync(shift.RotaId);
        if (rota is null) throw new InvalidOperationException("Rota not found.");

        var es = rota.EventSettings;
        if (shift.DayOffset < es.BuildStartOffset || shift.DayOffset > es.StrikeEndOffset)
            throw new InvalidOperationException(
                $"DayOffset {shift.DayOffset} is outside the valid range ({es.BuildStartOffset}..{es.StrikeEndOffset}).");

        if (shift.MinVolunteers > shift.MaxVolunteers)
            throw new InvalidOperationException("MinVolunteers cannot exceed MaxVolunteers.");

        shift.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.AddShiftAsync(shift);
    }

    public async Task UpdateShiftAsync(Shift shift)
    {
        shift.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.UpdateShiftAsync(shift);
    }

    public async Task DeleteShiftAsync(Guid shiftId)
    {
        var shift = await _repo.GetShiftWithSignupsForDeleteAsync(shiftId);
        if (shift is null) throw new InvalidOperationException("Shift not found.");

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete — {confirmedCount} humans have confirmed signups. Bail or reassign them first.");

        foreach (var signup in shift.ShiftSignups.Where(d => d.Status == SignupStatus.Pending))
        {
            signup.Cancel(_clock, "Shift deleted");
        }

        await _repo.DeleteShiftCascadeAsync(shiftId);
    }

    public Task<Shift?> GetShiftByIdAsync(Guid shiftId) =>
        _repo.GetShiftByIdAsync(shiftId);

    public Task<IReadOnlyList<Shift>> GetShiftsByRotaAsync(Guid rotaId) =>
        _repo.GetShiftsByRotaAsync(rotaId);

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
        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        int? dayOffset = null;
        int? minDayOffset = null;
        int? maxDayOffset = null;

        switch (period)
        {
            case ShiftPeriod.Build:
                maxDayOffset = -1;
                break;
            case ShiftPeriod.Event:
                minDayOffset = 0;
                maxDayOffset = es.EventEndOffset;
                break;
            case ShiftPeriod.Strike:
                minDayOffset = es.EventEndOffset + 1;
                break;
        }

        if (date.HasValue)
        {
            dayOffset = Period.Between(es.GateOpeningDate, date.Value, PeriodUnits.Days).Days;
        }

        var shifts = await _repo.GetShiftsWithSignupsForUrgencyAsync(
            eventSettingsId, departmentId, dayOffset, minDayOffset, maxDayOffset);

        // Resolve team names in one batch (no cross-domain Include).
        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIds);

        var now = _clock.GetCurrentInstant();
        var urgentShifts = shifts
            .Where(s => s.GetAbsoluteEnd(es) > now)
            .Select(s =>
            {
                var confirmedCount = s.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
                var score = CalculateScore(s, confirmedCount, es);
                var remaining = Math.Max(0, s.MaxVolunteers - confirmedCount);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var t) ? t.Name : string.Empty;
                return new UrgentShift(s, score, confirmedCount, remaining, teamName, []);
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
        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        int? fromOffset = fromDate.HasValue
            ? Period.Between(es.GateOpeningDate, fromDate.Value, PeriodUnits.Days).Days
            : null;
        int? toOffset = toDate.HasValue
            ? Period.Between(es.GateOpeningDate, toDate.Value, PeriodUnits.Days).Days
            : null;

        var shifts = await _repo.GetShiftsWithSignupsForEventAsync(
            eventSettingsId, departmentId, includeAdminOnly, includeHidden,
            fromOffset, toOffset, includeRotaTags: true);

        // Cross-domain lookups via services.
        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIds);

        IReadOnlyDictionary<Guid, User>? userLookup = null;
        if (includeSignups)
        {
            var userIds = shifts
                .SelectMany(s => s.ShiftSignups)
                .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .Select(ss => ss.UserId)
                .Distinct()
                .ToList();
            if (userIds.Count > 0)
                userLookup = await UserService.GetByIdsAsync(userIds);
        }

        return shifts
            .Select(s =>
            {
                var confirmedCount = s.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
                var score = CalculateScore(s, confirmedCount, es);
                var remaining = Math.Max(0, s.MaxVolunteers - confirmedCount);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var t) ? t.Name : string.Empty;
                var signups = includeSignups
                    ? s.ShiftSignups
                        .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                        .Select(ss =>
                        {
                            User? user = null;
                            userLookup?.TryGetValue(ss.UserId, out user);
                            return (
                                ss.UserId,
                                DisplayName: user?.DisplayName ?? string.Empty,
                                ss.Status,
                                HasProfilePicture: user?.ProfilePictureUrl is not null);
                        })
                        .OrderBy(ss => ss.Status == SignupStatus.Confirmed ? 0 : 1)
                        .ThenBy(ss => ss.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [];
                return new UrgentShift(s, score, confirmedCount, remaining, teamName, signups);
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

        var byPeriod = rankedShifts
            .GroupBy(u => u.Shift.GetShiftPeriod(es))
            .ToDictionary(g => g.Key, g => g.ToList());

        var reserved = new List<UrgentShift>();
        var reservedIds = new HashSet<Guid>();
        foreach (var period in new[] { ShiftPeriod.Event, ShiftPeriod.Strike })
        {
            if (byPeriod.TryGetValue(period, out var periodShifts) && periodShifts.Count > 0)
            {
                var best = periodShifts[0];
                reserved.Add(best);
                reservedIds.Add(best.Shift.Id);
            }
        }

        if (reserved.Count >= limit)
            return reserved.OrderByDescending(u => u.UrgencyScore).Take(limit).ToList();

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
        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
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

        var shifts = await _repo.GetShiftsForEventAsync(eventSettingsId, departmentId);

        // Need signup counts per shift (confirmed).
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var confirmedCounts = await _repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

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
            var confirmedCount = overlapping.Sum(s =>
                confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0);

            results.Add(new DailyStaffingData(dayOffset, dateLabel, confirmedCount, totalSlots, minSlots, periodLabel));
        }

        return results;
    }

    public async Task<IReadOnlyList<DailyStaffingHours>> GetStaffingHoursAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null)
    {
        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
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

        var shifts = await _repo.GetShiftsForEventAsync(eventSettingsId, departmentId);
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
        return await GetShiftsSummaryForTeamsAsync(eventSettingsId, new[] { departmentTeamId });
    }

    public async Task<ShiftsSummaryData?> GetShiftsSummaryForTeamsAsync(
        Guid eventSettingsId, IReadOnlyList<Guid> teamIds)
    {
        if (teamIds.Count == 0) return null;

        var rotas = await _repo.GetRotasWithShiftsAndSignupsAsync(eventSettingsId, teamIds);
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

    public Task<IReadOnlyList<Guid>> GetTeamIdsWithShiftsInEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default) =>
        _repo.GetTeamIdsWithShiftsInEventAsync(eventSettingsId, teamIds, ct);

    public async Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId)
    {
        var teamIds = await _repo.GetTeamIdsWithRotasInEventAsync(eventSettingsId);
        if (teamIds.Count == 0) return [];

        // GetByIdsWithParentsAsync also returns parent teams to enrich lookups,
        // but only the requested rota-owning teams should appear in the
        // department filter list. Otherwise parents with no rotas leak into the
        // UI dropdown and exact-TeamId filtering produces empty result pages.
        var rotaOwningIds = teamIds.ToHashSet();
        var teams = await TeamService.GetByIdsWithParentsAsync(teamIds);
        return teams.Values
            .Where(t => rotaOwningIds.Contains(t.Id))
            .Select(t => (t.Id, t.Name))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    // ============================================================
    // Coordinator Dashboard
    // ============================================================

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
        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null)
            return EmptyOverview();

        var allShifts = await _repo.GetVisibleShiftsForEventAsync(eventSettingsId);

        var shifts = period is null
            ? (IReadOnlyList<Shift>)allShifts
            : allShifts.Where(s => s.GetShiftPeriod(es) == period.Value).ToList();

        var shiftIds = shifts.Select(s => s.Id).ToList();
        var confirmedCountsRo = await _repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);
        var confirmedCounts = confirmedCountsRo.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        int ConfirmedOn(Guid shiftId) => confirmedCounts.TryGetValue(shiftId, out var c) ? c : 0;
        bool IsFilled(Shift s) => ConfirmedOn(s.Id) >= s.MinVolunteers;

        var totalShifts = shifts.Count;
        var filledShifts = shifts.Count(IsFilled);

        int FilledSlotsOn(Shift s) => Math.Min(ConfirmedOn(s.Id), s.MaxVolunteers);
        var totalSlots = shifts.Sum(s => s.MaxVolunteers);
        var filledSlots = shifts.Sum(FilledSlotsOn);

        PeriodBreakdown periodFillRates;
        {
            var perPeriod = shifts
                .GroupBy(s => s.GetShiftPeriod(es))
                .ToDictionary(g => g.Key, g => (TotalSlots: g.Sum(x => x.MaxVolunteers), FilledSlots: g.Sum(FilledSlotsOn)));
            periodFillRates = new PeriodBreakdown(
                Pct(perPeriod, ShiftPeriod.Build),
                Pct(perPeriod, ShiftPeriod.Event),
                Pct(perPeriod, ShiftPeriod.Strike));
        }

        var ticketHolderIds = await TicketQueryService.GetMatchedUserIdsForPaidOrdersAsync();
        var ticketHolders = ticketHolderIds as HashSet<Guid> ?? ticketHolderIds.ToHashSet();

        var engagedUserIds = await _repo.GetEngagedUserIdsForShiftsAsync(shiftIds);
        var engaged = engagedUserIds.ToHashSet();

        var ticketHoldersEngaged = engaged.Count(u => ticketHolders.Contains(u));
        var nonTicketSignups = engaged.Count(u => !ticketHolders.Contains(u));

        var staleThreshold = _clock.GetCurrentInstant().Minus(Duration.FromDays(3));
        var stalePendingCount = await _repo.GetStalePendingSignupCountAsync(shiftIds, staleThreshold);

        // Pull the teams referenced by our loaded rotas (plus their parents) via
        // ITeamService so we never navigate across domains.
        var teamIdsOnRotas = shifts
            .Select(s => s.Rota.TeamId)
            .Distinct()
            .ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIdsOnRotas);

        var departments = BuildDepartmentRows(shifts, confirmedCounts, es, teamLookup);

        return new DashboardOverview(
            totalShifts, filledShifts, totalSlots, filledSlots, periodFillRates,
            ticketHolders.Count, ticketHoldersEngaged, nonTicketSignups, stalePendingCount,
            departments);

        static double Pct(Dictionary<ShiftPeriod, (int TotalSlots, int FilledSlots)> perPeriod, ShiftPeriod p) =>
            perPeriod.TryGetValue(p, out var v) && v.TotalSlots > 0
                ? 100.0 * v.FilledSlots / v.TotalSlots
                : 0d;
    }

    private static DashboardOverview EmptyOverview() => new(
        0, 0, 0, 0, new PeriodBreakdown(0, 0, 0), 0, 0, 0, 0, Array.Empty<DepartmentStaffingRow>());

    private static List<DepartmentStaffingRow> BuildDepartmentRows(
        IReadOnlyList<Shift> shifts,
        Dictionary<Guid, int> confirmedCounts,
        EventSettings es,
        IReadOnlyDictionary<Guid, Team> teamLookup)
    {
        // Helper: resolve the department ID (parent team if any, else own team) for a shift.
        Guid DeptIdOf(Shift s)
        {
            if (!teamLookup.TryGetValue(s.Rota.TeamId, out var team))
                return s.Rota.TeamId; // Team row vanished — fall back to the rota's team id.
            return team.ParentTeamId ?? team.Id;
        }

        string NameOf(Guid teamId) =>
            teamLookup.TryGetValue(teamId, out var t) ? t.Name : string.Empty;

        string? SlugOf(Guid teamId) =>
            teamLookup.TryGetValue(teamId, out var t) ? t.Slug : null;

        var groups = shifts
            .GroupBy(DeptIdOf)
            .ToList();

        var rows = new List<DepartmentStaffingRow>();
        foreach (var g in groups)
        {
            var deptShifts = g.ToList();
            var deptId = g.Key;
            var deptName = NameOf(deptId);
            var agg = AggregateShifts(deptShifts, confirmedCounts, es);

            var subgroups = new List<SubgroupStaffingRow>();
            var anySubteam = deptShifts.Any(s =>
                teamLookup.TryGetValue(s.Rota.TeamId, out var t) && t.ParentTeamId != null);
            if (anySubteam)
            {
                var subteamGroups = deptShifts
                    .Where(s => teamLookup.TryGetValue(s.Rota.TeamId, out var t) && t.ParentTeamId != null)
                    .GroupBy(s => s.Rota.TeamId);
                foreach (var sg in subteamGroups)
                {
                    var sTeamId = sg.Key;
                    var sAgg = AggregateShifts(sg.ToList(), confirmedCounts, es);
                    subgroups.Add(new SubgroupStaffingRow(
                        sTeamId, NameOf(sTeamId), SlugOf(sTeamId), IsDirect: false,
                        sAgg.Total, sAgg.Filled, sAgg.TotalSlots, sAgg.FilledSlots, sAgg.Remaining,
                        sAgg.Build, sAgg.Event, sAgg.Strike));
                }

                var directShifts = deptShifts.Where(s => s.Rota.TeamId == deptId).ToList();
                if (directShifts.Count > 0)
                {
                    var dAgg = AggregateShifts(directShifts, confirmedCounts, es);
                    // "Direct" points at the parent department's own team page — that's
                    // where you'd manage direct roles/shifts on the parent.
                    subgroups.Insert(0, new SubgroupStaffingRow(
                        deptId, "Direct", SlugOf(deptId), IsDirect: true,
                        dAgg.Total, dAgg.Filled, dAgg.TotalSlots, dAgg.FilledSlots, dAgg.Remaining,
                        dAgg.Build, dAgg.Event, dAgg.Strike));
                }

                subgroups = subgroups
                    .OrderByDescending(r => r.IsDirect)
                    .ThenBy(r => r.TotalSlots == 0 ? 0 : 100.0 * r.FilledSlots / r.TotalSlots)
                    .ThenBy(r => r.Name, StringComparer.Ordinal)
                    .ToList();
            }

            rows.Add(new DepartmentStaffingRow(
                deptId, deptName, SlugOf(deptId),
                agg.Total, agg.Filled, agg.TotalSlots, agg.FilledSlots, agg.Remaining,
                agg.Build, agg.Event, agg.Strike,
                subgroups));
        }

        return rows
            .OrderBy(r => r.TotalSlots == 0 ? 0 : 100.0 * r.FilledSlots / r.TotalSlots)
            .ThenBy(r => r.DepartmentName, StringComparer.Ordinal)
            .ToList();
    }

    private static (int Total, int Filled, int TotalSlots, int FilledSlots, int Remaining, PeriodStaffing Build, PeriodStaffing Event, PeriodStaffing Strike)
        AggregateShifts(List<Shift> shifts, Dictionary<Guid, int> confirmedCounts, EventSettings es)
    {
        int total = shifts.Count;
        int filled = 0;
        int totalSlots = 0;
        int filledSlots = 0;
        int remaining = 0;
        var periodAgg = new Dictionary<ShiftPeriod, (int Total, int Filled, int TotalSlots, int FilledSlots, int Remaining)>
        {
            [ShiftPeriod.Build] = (0, 0, 0, 0, 0),
            [ShiftPeriod.Event] = (0, 0, 0, 0, 0),
            [ShiftPeriod.Strike] = (0, 0, 0, 0, 0),
        };

        foreach (var s in shifts)
        {
            var confirmed = confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0;
            var isFilled = confirmed >= s.MinVolunteers;
            var filledSlotsHere = Math.Min(confirmed, s.MaxVolunteers);
            var slotsLeft = Math.Max(0, s.MaxVolunteers - confirmed);

            if (isFilled) filled++;
            totalSlots += s.MaxVolunteers;
            filledSlots += filledSlotsHere;
            remaining += slotsLeft;

            var p = s.GetShiftPeriod(es);
            var cur = periodAgg[p];
            periodAgg[p] = (
                cur.Total + 1,
                cur.Filled + (isFilled ? 1 : 0),
                cur.TotalSlots + s.MaxVolunteers,
                cur.FilledSlots + filledSlotsHere,
                cur.Remaining + slotsLeft);
        }

        PeriodStaffing ToStaffing(ShiftPeriod p)
        {
            var v = periodAgg[p];
            return new PeriodStaffing(v.Total, v.Filled, v.TotalSlots, v.FilledSlots, v.Remaining);
        }

        return (total, filled, totalSlots, filledSlots, remaining, ToStaffing(ShiftPeriod.Build), ToStaffing(ShiftPeriod.Event), ToStaffing(ShiftPeriod.Strike));
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
        int? minDayOffset = null;
        int? maxDayOffset = null;

        if (period is not null)
        {
            var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
            if (es is null) return Array.Empty<CoordinatorActivityRow>();
            switch (period.Value)
            {
                case ShiftPeriod.Build:
                    maxDayOffset = -1;
                    break;
                case ShiftPeriod.Event:
                    minDayOffset = 0;
                    maxDayOffset = es.EventEndOffset;
                    break;
                case ShiftPeriod.Strike:
                    minDayOffset = es.EventEndOffset + 1;
                    break;
            }
        }

        var pendingCounts = await _repo.GetPendingSignupCountsByTeamAsync(
            eventSettingsId, minDayOffset, maxDayOffset);

        if (pendingCounts.Count == 0)
            return Array.Empty<CoordinatorActivityRow>();

        // Load team metadata for pending teams and walk up through parents until we
        // have every ancestor in our working set. GetByIdsWithParentsAsync includes
        // one level of parents; deeper chains iterate until fixed-point.
        var teamMeta = new Dictionary<Guid, Team>();
        var pendingFetch = pendingCounts.Keys.ToHashSet();
        while (pendingFetch.Count > 0)
        {
            var toFetch = pendingFetch.Where(id => !teamMeta.ContainsKey(id)).ToList();
            if (toFetch.Count == 0) break;

            var fetched = await TeamService.GetByIdsWithParentsAsync(toFetch);
            foreach (var kvp in fetched)
            {
                teamMeta[kvp.Key] = kvp.Value;
            }

            pendingFetch = fetched.Values
                .Where(t => t.ParentTeamId.HasValue && !teamMeta.ContainsKey(t.ParentTeamId.Value))
                .Select(t => t.ParentTeamId!.Value)
                .ToHashSet();
        }

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

        var coordsRaw = await TeamService.GetActiveCoordinatorsForTeamsAsync(relevantTeamIds.ToList());
        var coordinatorUserIds = coordsRaw.Select(c => c.UserId).Distinct().ToList();

        var userLookup = await UserService.GetByIdsAsync(coordinatorUserIds);

        var coordsByTeam = coordsRaw
            .GroupBy(c => c.TeamId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CoordinatorLogin>)g
                    .Select(c =>
                    {
                        userLookup.TryGetValue(c.UserId, out var user);
                        return new CoordinatorLogin(
                            c.UserId,
                            user?.DisplayName ?? string.Empty,
                            user?.LastLoginAt);
                    })
                    .OrderBy(c => c.LastLoginAt ?? Instant.MinValue)
                    .ToList());

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
        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
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

        int? minDayOffset = null;
        int? maxDayOffset = null;
        switch (period)
        {
            case ShiftPeriod.Build:
                maxDayOffset = -1;
                break;
            case ShiftPeriod.Event:
                minDayOffset = 0;
                maxDayOffset = es.EventEndOffset;
                break;
            case ShiftPeriod.Strike:
                minDayOffset = es.EventEndOffset + 1;
                break;
        }

        var signupsInWindow = await _repo.GetSignupCreatedAtsInWindowAsync(
            eventSettingsId, startInstant, endInstant, minDayOffset, maxDayOffset);

        var ticketsInWindow = await TicketQueryService.GetPaidOrderDatesInWindowAsync(startInstant, endInstant);
        var loginsInWindow = await UserService.GetLoginTimestampsInWindowAsync(startInstant, endInstant);

        LocalDate ToLocalDate(Instant i) => i.InZone(tz).Date;

        var signupsByDay = signupsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
        var ticketsByDay = ticketsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
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

    public async Task<IReadOnlyList<DailyDepartmentStaffing>> GetDailyDepartmentStaffingAsync(
        Guid eventSettingsId, ShiftPeriod? period)
    {
        // Only meaningful for Set-up (Build) and Strike. Event planning has a different
        // day-over-day dynamic (per-rota shift-time coverage), so we intentionally skip it.
        if (period is not (ShiftPeriod.Build or ShiftPeriod.Strike))
            return Array.Empty<DailyDepartmentStaffing>();

        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return Array.Empty<DailyDepartmentStaffing>();

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;

        var dayOffsets = new List<int>();
        if (period is ShiftPeriod.Build)
            for (var d = es.BuildStartOffset; d < 0; d++) dayOffsets.Add(d);
        else
            for (var d = es.EventEndOffset + 1; d <= es.StrikeEndOffset; d++) dayOffsets.Add(d);

        if (dayOffsets.Count == 0) return Array.Empty<DailyDepartmentStaffing>();

        var shifts = await _repo.GetVisibleShiftsForEventAsync(eventSettingsId);
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var confirmedCounts = await _repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

        var teamIdsOnRotas = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIdsOnRotas);

        (Guid Id, string Name) DeptOf(Shift s)
        {
            if (!teamLookup.TryGetValue(s.Rota.TeamId, out var t))
                return (s.Rota.TeamId, string.Empty);
            var parentId = t.ParentTeamId ?? t.Id;
            var parentName = t.ParentTeamId is null
                ? t.Name
                : teamLookup.TryGetValue(parentId, out var parent) ? parent.Name : t.Name;
            return (parentId, parentName);
        }

        var results = new List<DailyDepartmentStaffing>(dayOffsets.Count);
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
            });

            var byDept = new Dictionary<Guid, (string Name, int Count)>();
            foreach (var s in overlapping)
            {
                var (deptId, deptName) = DeptOf(s);
                var confirmed = confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0;
                if (confirmed == 0) continue;
                var cur = byDept.TryGetValue(deptId, out var existing) ? existing : (Name: deptName, Count: 0);
                byDept[deptId] = (cur.Name, cur.Count + confirmed);
            }

            var depts = byDept.Values
                .OrderBy(v => v.Name, StringComparer.Ordinal)
                .Select(v => new DepartmentDayCount(v.Name, v.Count))
                .ToList();

            results.Add(new DailyDepartmentStaffing(dayDate, dateLabel, depts));
        }

        return results;
    }

    public async Task<CoverageHeatmap> GetCoverageHeatmapAsync(
        Guid eventSettingsId, ShiftPeriod? period)
    {
        var empty = new CoverageHeatmap(
            Array.Empty<CoverageHeatmapDay>(),
            Array.Empty<CoverageHeatmapRotaRow>());

        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return empty;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;

        var dayOffsets = new List<int>();
        if (period is null or ShiftPeriod.Build)
            for (var d = es.BuildStartOffset; d < 0; d++) dayOffsets.Add(d);
        if (period is null or ShiftPeriod.Event)
            for (var d = 0; d <= es.EventEndOffset; d++) dayOffsets.Add(d);
        if (period is null or ShiftPeriod.Strike)
            for (var d = es.EventEndOffset + 1; d <= es.StrikeEndOffset; d++) dayOffsets.Add(d);

        if (dayOffsets.Count == 0) return empty;

        var allShifts = await _repo.GetVisibleShiftsForEventAsync(eventSettingsId);
        if (allShifts.Count == 0) return empty;

        var shiftIds = allShifts.Select(s => s.Id).ToList();
        var confirmedCounts = await _repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

        var teamIds = allShifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIds);

        var days = dayOffsets
            .Select(off =>
            {
                var date = es.GateOpeningDate.PlusDays(off);
                var label = date.DayOfWeek.ToString()[..3] + " " + date.ToString("MMM d", null);
                var dayPeriod = off < 0 ? ShiftPeriod.Build : off <= es.EventEndOffset ? ShiftPeriod.Event : ShiftPeriod.Strike;
                return new CoverageHeatmapDay(off, date, label, dayPeriod);
            })
            .ToList();

        // Resolves the TEAM a shift should be displayed under on the heatmap.
        //   - Shift on a top-level team → that team
        //   - Shift on a PROMOTED subteam → that subteam (gets its own row)
        //   - Shift on a non-promoted subteam → roll up into the parent team
        Team? DisplayTeamFor(Shift s)
        {
            if (!teamLookup.TryGetValue(s.Rota.TeamId, out var team)) return null;
            if (team.ParentTeamId is null) return team;
            if (team.IsPromotedToDirectory) return team;
            return team.ParentTeamId is Guid pid && teamLookup.TryGetValue(pid, out var parent) ? parent : null;
        }

        // Group shifts by the display team (one row per team on the heatmap). A team
        // qualifies for its own row if it is in the directory — top-level teams always
        // are, subteams only if IsPromotedToDirectory (aka "Show on Teams page").
        var shiftsByDisplayTeam = allShifts
            .Select(s => (Shift: s, Team: DisplayTeamFor(s)))
            .Where(x => x.Team?.IsInDirectory == true)
            .GroupBy(x => x.Team!.Id, x => x.Shift)
            .ToList();

        var rows = new List<CoverageHeatmapRotaRow>();
        foreach (var teamGroup in shiftsByDisplayTeam)
        {
            var teamId = teamGroup.Key;
            if (!teamLookup.TryGetValue(teamId, out var team)) continue;

            var shifts = teamGroup.ToList();

            var cells = new List<CoverageHeatmapCell>(days.Count);
            var teamHasAnyShift = false;

            foreach (var day in days)
            {
                var dayStart = day.Date.AtStartOfDayInZone(tz).ToInstant();
                var dayEnd = day.Date.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();

                var overlapping = shifts.Where(s =>
                {
                    var start = s.GetAbsoluteStart(es);
                    var end = s.GetAbsoluteEnd(es);
                    return start < dayEnd && end > dayStart;
                }).ToList();

                var totalSlots = overlapping.Sum(s => s.MaxVolunteers);
                var filledSlots = overlapping.Sum(s =>
                    Math.Min(confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0, s.MaxVolunteers));

                if (totalSlots > 0) teamHasAnyShift = true;
                cells.Add(new CoverageHeatmapCell(day.DayOffset, totalSlots, filledSlots));
            }

            if (!teamHasAnyShift) continue;

            // For a promoted subteam row, carry its parent's name as the "department"
            // tag so coordinators see which top-level team it rolls into.
            var departmentName = team.ParentTeamId is Guid pid && teamLookup.TryGetValue(pid, out var parentTeam)
                ? parentTeam.Name
                : team.Name;

            rows.Add(new CoverageHeatmapRotaRow(
                team.Id,
                team.Name,
                departmentName,
                cells));
        }

        rows = rows
            .OrderBy(r => r.DepartmentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RotaName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CoverageHeatmap(days, rows);
    }

    public async Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default)
    {
        var es = await _repo.GetActiveEventSettingsAsync(ct);
        if (es is null) return (0, 0, 0d);

        var allShifts = await _repo.GetVisibleShiftsForEventAsync(es.Id, ct);
        if (allShifts.Count == 0) return (0, 0, 0d);

        var shiftIds = allShifts.Select(s => s.Id).ToList();
        var confirmedCounts = await _repo.GetConfirmedSignupCountsByShiftAsync(shiftIds, ct);

        var total = allShifts.Sum(s => s.MaxVolunteers);
        var filled = allShifts.Sum(s =>
            Math.Min(confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0, s.MaxVolunteers));

        var ratio = total == 0 ? 0d : (double)filled / total;
        return (filled, total, ratio);
    }

    public async Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period)
    {
        if (period is null) return Array.Empty<ShiftDurationBreakdownRow>();

        var es = await _repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return Array.Empty<ShiftDurationBreakdownRow>();

        var allShifts = await _repo.GetVisibleShiftsForEventAsync(eventSettingsId);
        var periodShifts = allShifts.Where(s => s.GetShiftPeriod(es) == period.Value).ToList();

        var shiftIds = periodShifts.Select(s => s.Id).ToList();
        var confirmedCounts = await _repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

        int FilledSlotsOn(Shift s)
        {
            var confirmed = confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0;
            return Math.Min(confirmed, s.MaxVolunteers);
        }

        // Key: (IsAllDay, whole-hour Duration). All-day shifts collapse into one bucket
        // regardless of nominal duration; hourly shifts are rounded down to whole hours.
        var grouped = periodShifts
            .GroupBy(s => (
                s.IsAllDay,
                Hours: s.IsAllDay ? 0 : (int)s.Duration.TotalHours))
            .Select(g => new ShiftDurationBreakdownRow(
                IsAllDay: g.Key.IsAllDay,
                DurationHours: g.Key.Hours,
                TotalSlots: g.Sum(s => s.MaxVolunteers),
                FilledSlots: g.Sum(FilledSlotsOn)))
            // Full-day on top; hourly shifts sorted ascending by duration.
            .OrderByDescending(r => r.IsAllDay)
            .ThenBy(r => r.DurationHours)
            .ToList();

        return grouped;
    }

    // ============================================================
    // Shift Tags
    // ============================================================

    public Task<IReadOnlyList<ShiftTag>> GetAllTagsAsync() =>
        _repo.GetAllTagsAsync();

    public Task<IReadOnlyList<ShiftTag>> SearchTagsAsync(string query) =>
        _repo.SearchTagsAsync(query);

    public async Task<ShiftTag> GetOrCreateTagAsync(string name)
    {
        var trimmed = name.Trim();
        var existing = await _repo.FindTagByNameAsync(trimmed);
        if (existing is not null) return existing;

        var tag = new ShiftTag
        {
            Id = Guid.NewGuid(),
            Name = trimmed
        };
        await _repo.AddTagAsync(tag);
        return tag;
    }

    public Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds) =>
        _repo.SetRotaTagsAsync(rotaId, tagIds);

    public Task<IReadOnlyList<ShiftTag>> GetVolunteerTagPreferencesAsync(Guid userId) =>
        _repo.GetVolunteerTagPreferencesAsync(userId);

    public Task SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds) =>
        _repo.SetVolunteerTagPreferencesAsync(userId, tagIds);

    public async Task<IReadOnlyDictionary<Guid, int>> GetPendingShiftSignupCountsByTeamAsync(
        Guid eventSettingsId,
        CancellationToken cancellationToken = default)
    {
        var activeEventId = await _repo.GetActiveEventIdAsync(eventSettingsId, cancellationToken);
        if (activeEventId == Guid.Empty)
            return new Dictionary<Guid, int>();

        return await _repo.GetPendingSignupCountsByTeamAsync(activeEventId, null, null, cancellationToken);
    }

    // ============================================================
    // Volunteer Event Profiles
    // ============================================================

    public async Task<VolunteerEventProfile> GetOrCreateShiftProfileAsync(Guid userId)
    {
        var existing = await _repo.GetVolunteerEventProfileAsync(userId);
        if (existing is not null) return existing;

        var now = _clock.GetCurrentInstant();
        var profile = new VolunteerEventProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repo.AddVolunteerEventProfileAsync(profile);
        return profile;
    }

    public async Task UpdateShiftProfileAsync(VolunteerEventProfile profile)
    {
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _repo.UpdateVolunteerEventProfileAsync(profile);
    }

    public async Task<VolunteerEventProfile?> GetShiftProfileAsync(Guid userId, bool includeMedical)
    {
        var profile = await _repo.GetVolunteerEventProfileAsync(userId);

        if (profile is not null && !includeMedical)
        {
            profile.MedicalConditions = null;
        }

        return profile;
    }

    public Task<int> DeleteShiftProfilesForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        _repo.DeleteVolunteerEventProfilesForUserAsync(userId, ct);
}
