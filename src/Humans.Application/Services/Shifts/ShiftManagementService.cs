using Humans.Application.DTOs;
using Humans.Application.Enums;
using Humans.Application.Extensions;
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
using Humans.Domain.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Consolidated shift-management (authorization, event settings, rotas, shifts, urgency,
/// coordinator dashboard, shift tags, volunteer event profiles).
/// Caches: 60s coordinator-team-ids (hot path, §15f); 5min dashboard. No full-grid cache.
/// </summary>
public sealed class ShiftManagementService(
    IShiftManagementRepository repo,
    IAuditLogService auditLogService,
    IAdminAuthorizationService adminAuthorization,
    IServiceProvider serviceProvider,
    IMemoryCache cache,
    IShiftViewInvalidator viewInvalidator,
    IClock clock,
    ILogger<ShiftManagementService> logger) : IShiftManagementService, IShiftAuthorizationInvalidator, IUserMerge
{
    private static readonly TimeSpan AuthCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<ShiftPriority, double> PriorityWeights = new()
    {
        [ShiftPriority.Normal] = 1,
        [ShiftPriority.Important] = 3,
        [ShiftPriority.Essential] = 6
    };

    // Width of the all-day shift window, used by pie-row math.
    private static readonly decimal AllDayShiftHours = (decimal)Duration.FromTicks(
        Shift.AllDayWindowEnd.TickOfDay - Shift.AllDayWindowStart.TickOfDay).TotalHours;

    private readonly ILogger<ShiftManagementService> _logger = logger;

    // Lazy-resolved to break DI cycles (TeamService → this → TeamService etc.).
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IRoleAssignmentService RoleAssignmentService => serviceProvider.GetRequiredService<IRoleAssignmentService>();
    private ITicketServiceRead TicketQueryService => serviceProvider.GetRequiredService<ITicketServiceRead>();
    private IUserServiceRead UserService => serviceProvider.GetRequiredService<IUserServiceRead>();

    public async Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId)
    {
        var teamIds = await GetCoordinatorTeamIdsAsync(userId);
        if (teamIds.Contains(departmentTeamId))
            return true;

        // Parent department coordinators can manage child teams
        var team = await TeamService.GetTeamByIdAsync(departmentTeamId);
        return team?.ParentTeamId is not null && teamIds.Contains(team.ParentTeamId.Value);
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
        var result = await cache.GetOrCreateAsync(CacheKeys.ShiftAuthorization(userId), async entry =>
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
    /// </summary>
    public void Invalidate(Guid userId)
    {
        cache.Remove(CacheKeys.ShiftAuthorization(userId));
    }


    public Task<EventSettings?> GetActiveAsync() =>
        repo.GetActiveEventSettingsAsync();

    public Task<EventSettings?> GetByIdAsync(Guid id) =>
        repo.GetEventSettingsByIdAsync(id);

    public async Task CreateAsync(EventSettings entity)
    {
        if (entity.IsActive)
        {
            var conflict = await repo.AnyOtherActiveEventSettingsAsync(excludingId: null);
            if (conflict)
                throw new InvalidOperationException("Only one EventSettings can be active at a time.");
        }

        entity.UpdatedAt = clock.GetCurrentInstant();
        await repo.AddEventSettingsAsync(entity);
        // Activation can flip the active event; every ShiftUserView is event-scoped.
        if (entity.IsActive)
            viewInvalidator.InvalidateAll();
    }

    public async Task UpdateAsync(EventSettings entity)
    {
        if (entity.IsActive)
        {
            var conflict = await repo.AnyOtherActiveEventSettingsAsync(excludingId: entity.Id);
            if (conflict)
                throw new InvalidOperationException("Only one EventSettings can be active at a time.");
        }

        entity.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateEventSettingsAsync(entity);

        EvictDashboardCaches(entity.Id);
        viewInvalidator.InvalidateAll();
    }

    public async Task<int> DeleteEventAsync(
        Guid eventSettingsId,
        CancellationToken cancellationToken = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(cancellationToken);
        var deleted = await repo.DeleteEventCascadeAsync(eventSettingsId, cancellationToken);
        EvictDashboardCaches(eventSettingsId);
        viewInvalidator.InvalidateAll();
        return deleted;
    }


    public async Task CreateRotaAsync(Rota rota)
    {
        var team = await TeamService.GetTeamByIdAsync(rota.TeamId);

        if (team is null)
            throw new InvalidOperationException("Team not found.");
        if (team.SystemTeamType != SystemTeamType.None)
            throw new InvalidOperationException("Rotas cannot be created on system teams.");

        var eventSettings = await repo.GetEventSettingsByIdAsync(rota.EventSettingsId);
        if (eventSettings is null || !eventSettings.IsActive)
            throw new InvalidOperationException("Active EventSettings not found.");

        rota.UpdatedAt = clock.GetCurrentInstant();
        await repo.AddRotaAsync(rota);
        viewInvalidator.InvalidateRota(rota.Id);
    }

    public async Task UpdateRotaAsync(Rota rota)
    {
        rota.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateRotaAsync(rota);
        viewInvalidator.InvalidateRota(rota.Id);
    }

    public async Task<RotaMoveResult> MoveRotaToTeamAsync(MoveRotaInput input)
    {
        var rota = await repo.GetRotaForUpdateAsync(input.RotaId);
        if (rota is null || rota.TeamId != input.SourceTeamId)
            return RotaMoveResult.Failure("Rota not found.");

        var targetTeam = await TeamService.GetTeamByIdAsync(input.TargetTeamId);
        if (targetTeam is null)
            return RotaMoveResult.Failure("Target team not found.");
        if (targetTeam.ParentTeamId is not null)
            return RotaMoveResult.Failure("Rotas can only be moved to parent teams (departments).");
        if (targetTeam.SystemTeamType != SystemTeamType.None)
            return RotaMoveResult.Failure("Rotas cannot be moved to system teams.");
        if (rota.TeamId == input.TargetTeamId)
            return RotaMoveResult.Failure("Rota is already in this team.");

        // Fetch the old team name via ITeamService - no cross-domain Include.
        var oldTeam = await TeamService.GetTeamByIdAsync(rota.TeamId);
        var oldTeamName = oldTeam?.Name ?? "(unknown)";

        // Targeted write: only TeamId + UpdatedAt are marked modified so concurrent edits don't clobber.
        await repo.UpdateRotaTeamAssignmentAsync(
            rota.Id, input.TargetTeamId, clock.GetCurrentInstant());
        viewInvalidator.InvalidateRota(rota.Id);

        await auditLogService.LogAsync(
            AuditAction.RotaMovedToTeam, nameof(Rota), rota.Id,
            $"Moved rota '{rota.Name}' from '{oldTeamName}' to '{targetTeam.Name}'",
            input.ActorUserId,
            relatedEntityId: input.TargetTeamId, relatedEntityType: nameof(Team));

        return RotaMoveResult.Success($"Rota '{rota.Name}' moved to {targetTeam.Name}.", targetTeam.Slug);
    }
    public async Task DeleteRotaAsync(Guid rotaId)
    {
        var rota = await repo.GetRotaWithShiftsAndSignupsForDeleteAsync(rotaId);
        if (rota is null) throw new InvalidOperationException("Rota not found.");

        var confirmedCount = rota.Shifts
            .SelectMany(s => s.ShiftSignups)
            .Count(d => d.Status == SignupStatus.Confirmed);

        if (confirmedCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete — {confirmedCount} humans have confirmed signups. Bail or reassign them first.");

        // Cancel pending signups before cascade delete (ShiftSignup→Shift FK is Restrict).
        foreach (var shift in rota.Shifts)
        {
            foreach (var signup in shift.ShiftSignups.Where(d => d.Status == SignupStatus.Pending).ToList())
            {
                signup.Cancel(clock, "Rota deleted");
            }
        }

        // Snapshot user-ids pre-delete so cache eviction works after cascade.
        var affectedUserIds = rota.Shifts
            .SelectMany(s => s.ShiftSignups)
            .Select(d => d.UserId)
            .Distinct()
            .ToList();

        await repo.DeleteRotaCascadeAsync(rotaId);

        viewInvalidator.InvalidateRota(rotaId);
        foreach (var uid in affectedUserIds)
            viewInvalidator.InvalidateUser(uid);
    }

    public Task<Rota?> GetRotaByIdAsync(Guid rotaId) =>
        repo.GetRotaByIdWithShiftsAsync(rotaId);

    public Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId) =>
        repo.GetRotasByDepartmentAsync(teamId, eventSettingsId);

    public async Task<IReadOnlyList<RotaSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default)
    {
        if (Guid.TryParse(query, out var id))
        {
            var rota = await repo.GetRotaByIdWithShiftsAsync(id);
            if (rota is null) return [];
            var teams = await TeamService.GetTeamsAsync(cancellationToken);
            var teamName = teams.TryGetValue(rota.TeamId, out var t) ? t.Name : string.Empty;
            return [new RotaSearchHit(rota.Name, rota.TeamId, teamName)];
        }

        var settings = await repo.GetActiveEventSettingsAsync(cancellationToken);
        if (settings is null) return [];

        var rotas = await repo.SearchRotasAsync(
            query, settings.Id,
            onlyVolunteerVisible: true,
            max, cancellationToken);
        if (rotas.Count == 0) return [];

        // Stitch team names via ITeamService — cross-domain (§6).
        var teamIds = rotas.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await TeamService.GetTeamsAsync(cancellationToken);
        var teamNames = teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);

        return rotas
            .Select(r => new RotaSearchHit(
                r.Name,
                r.TeamId,
                teamNames.TryGetValue(r.TeamId, out var name) ? name : string.Empty))
            .ToList();
    }


    /// <summary>Re-export of <see cref="Shift.AllDayWindowStart"/>.</summary>
    public static LocalTime AllDayShiftStartTime => Shift.AllDayWindowStart;

    /// <summary>Re-export of <see cref="Shift.AllDayWindowEnd"/>.</summary>
    public static LocalTime AllDayShiftEndTime => Shift.AllDayWindowEnd;

    public async Task<ShiftGenerationResult> CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput input)
    {
        var rota = await repo.GetRotaWithEventSettingsAsync(input.RotaId);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftGenerationResult.Failure("Rota not found.");

        if (rota.Period == RotaPeriod.Event)
            return ShiftGenerationResult.Failure("Build/strike shift generation is only for Build or Strike rotas.");

        var dailyStaffing = input.Days
            .GroupBy(d => d.DayOffset)
            .ToDictionary(
                g => g.Key,
                g => (Min: g.Last().MinVolunteers, Max: g.Last().MaxVolunteers));

        if (dailyStaffing.Count == 0)
            return ShiftGenerationResult.Failure("At least one staffing day is required.");

        if (dailyStaffing.Values.Any(d => d.Min > d.Max))
            return ShiftGenerationResult.Failure("MinVolunteers cannot exceed MaxVolunteers.");

        var es = rota.EventSettings;

        foreach (var dayOffset in dailyStaffing.Keys)
        {
            if (rota.Period == RotaPeriod.Build && (dayOffset < es.BuildStartOffset || dayOffset >= 0))
                throw new InvalidOperationException($"Day offset {dayOffset} is outside the build period ({es.BuildStartOffset} to -1)");
            if (rota.Period == RotaPeriod.Strike && (dayOffset <= es.EventEndOffset || dayOffset > es.StrikeEndOffset))
                throw new InvalidOperationException($"Day offset {dayOffset} is outside the strike period ({es.EventEndOffset + 1} to {es.StrikeEndOffset})");
        }

        // Skip days that already have shifts (additive mode)
        var existingDayOffsets = await repo.GetShiftDayOffsetsForRotaAsync(input.RotaId);
        var existingSet = existingDayOffsets.ToHashSet();

        var now = clock.GetCurrentInstant();
        var toInsert = new List<Shift>();

        foreach (var (dayOffset, staffing) in dailyStaffing.Where(d => !existingSet.Contains(d.Key)).OrderBy(d => d.Key))
        {
            toInsert.Add(new Shift
            {
                Id = Guid.NewGuid(),
                RotaId = input.RotaId,
                IsAllDay = true,
                DayOffset = dayOffset,
                // StartTime/Duration are don't-care for IsAllDay rows; store midnight/24h as neutral sentinel.
                StartTime = LocalTime.Midnight,
                Duration = Duration.FromHours(24),
                MinVolunteers = staffing.Min,
                MaxVolunteers = staffing.Max,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        if (toInsert.Count > 0)
        {
            await repo.AddShiftsAsync(toInsert);
            viewInvalidator.InvalidateRota(input.RotaId);
        }

        return ShiftGenerationResult.Success($"Created {toInsert.Count} shifts for '{rota.Name}'.", toInsert.Count);
    }

    public async Task<ShiftGenerationResult> GenerateEventShiftsAsync(GenerateEventShiftsInput input)
    {
        var rota = await repo.GetRotaWithEventSettingsAsync(input.RotaId);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftGenerationResult.Failure("Rota not found.");

        if (rota.Period != RotaPeriod.Event)
            return ShiftGenerationResult.Failure("Event shift generation is only for Event-period rotas.");

        var es = rota.EventSettings;
        if (input.StartDayOffset < 0 ||
            input.EndDayOffset > es.EventEndOffset ||
            input.StartDayOffset > input.EndDayOffset)
            return ShiftGenerationResult.Failure("Shift dates must fall within the event period.");

        if (input.MinVolunteers > input.MaxVolunteers)
            return ShiftGenerationResult.Failure("MinVolunteers cannot exceed MaxVolunteers.");

        if (input.TimeSlots.Count == 0)
            return ShiftGenerationResult.Failure("At least one time slot is required.");

        var now = clock.GetCurrentInstant();
        var toInsert = new List<Shift>();

        for (var day = input.StartDayOffset; day <= input.EndDayOffset; day++)
        {
            foreach (var slot in input.TimeSlots)
            {
                toInsert.Add(new Shift
                {
                    Id = Guid.NewGuid(),
                    RotaId = input.RotaId,
                    IsAllDay = false,
                    DayOffset = day,
                    StartTime = slot.StartTime,
                    Duration = Duration.FromHours(slot.DurationHours),
                    MinVolunteers = input.MinVolunteers,
                    MaxVolunteers = input.MaxVolunteers,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        if (toInsert.Count > 0)
        {
            await repo.AddShiftsAsync(toInsert);
            viewInvalidator.InvalidateRota(input.RotaId);
        }

        return ShiftGenerationResult.Success($"Generated {toInsert.Count} shifts for '{rota.Name}'.", toInsert.Count);
    }


    public async Task<ShiftMutationResult> CreateShiftAsync(CreateShiftInput input)
    {
        var rota = await repo.GetRotaWithEventSettingsAsync(input.RotaId);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftMutationResult.Failure("Rota not found.");

        var es = rota.EventSettings;
        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(rota.Period, es);
        if (input.DayOffset < periodStart || input.DayOffset > periodEnd)
            return ShiftMutationResult.Failure("Shift date must fall within the rota's period.");

        if (input.MinVolunteers > input.MaxVolunteers)
            return ShiftMutationResult.Failure("MinVolunteers cannot exceed MaxVolunteers.");

        var now = clock.GetCurrentInstant();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = input.RotaId,
            Description = input.Description,
            DayOffset = input.DayOffset,
            StartTime = input.StartTime,
            Duration = Duration.FromHours(input.DurationHours),
            MinVolunteers = input.MinVolunteers,
            MaxVolunteers = input.MaxVolunteers,
            AdminOnly = input.AdminOnly,
            IsAllDay = input.IsAllDay,
            CreatedAt = now,
            UpdatedAt = now
        };

        await repo.AddShiftAsync(shift);
        viewInvalidator.InvalidateRota(input.RotaId);
        return ShiftMutationResult.Success("Shift created.", shift.Id);
    }

    private static (int Start, int End) GetRotaDayOffsetBounds(RotaPeriod period, EventSettings es) =>
        period switch
        {
            RotaPeriod.Build => (es.BuildStartOffset, -1),
            RotaPeriod.Event => (0, es.EventEndOffset),
            RotaPeriod.Strike => (es.EventEndOffset + 1, es.StrikeEndOffset),
            _ => (es.BuildStartOffset, es.StrikeEndOffset)
        };

    public async Task<ShiftMutationResult> UpdateShiftAsync(UpdateShiftInput input)
    {
        var shift = await repo.GetShiftByIdAsync(input.ShiftId);
        if (shift is null || shift.Rota.TeamId != input.TeamId)
            return ShiftMutationResult.Failure("Shift not found.");

        var es = shift.Rota.EventSettings;
        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(shift.Rota.Period, es);
        if (input.DayOffset < periodStart || input.DayOffset > periodEnd)
            return ShiftMutationResult.Failure("Shift date must fall within the rota's period.");

        if (input.MinVolunteers > input.MaxVolunteers)
            return ShiftMutationResult.Failure("MinVolunteers cannot exceed MaxVolunteers.");

        shift.Description = input.Description;
        shift.DayOffset = input.DayOffset;
        shift.StartTime = input.StartTime;
        shift.Duration = Duration.FromHours(input.DurationHours);
        shift.MinVolunteers = input.MinVolunteers;
        shift.MaxVolunteers = input.MaxVolunteers;
        shift.AdminOnly = input.AdminOnly;
        shift.UpdatedAt = clock.GetCurrentInstant();

        await repo.UpdateShiftAsync(shift);
        viewInvalidator.InvalidateShift(shift.Id);
        return ShiftMutationResult.Success("Shift updated.", shift.Id);
    }

    public async Task DeleteShiftAsync(Guid shiftId)
    {
        var shift = await repo.GetShiftWithSignupsForDeleteAsync(shiftId);
        if (shift is null) throw new InvalidOperationException("Shift not found.");

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete — {confirmedCount} humans have confirmed signups. Bail or reassign them first.");

        foreach (var signup in shift.ShiftSignups.Where(d => d.Status == SignupStatus.Pending))
        {
            signup.Cancel(clock, "Shift deleted");
        }

        var rotaId = shift.RotaId;
        var affectedUserIds = shift.ShiftSignups.Select(d => d.UserId).Distinct().ToList();

        await repo.DeleteShiftCascadeAsync(shiftId);

        viewInvalidator.InvalidateShift(shiftId);
        viewInvalidator.InvalidateRota(rotaId);
        foreach (var uid in affectedUserIds)
            viewInvalidator.InvalidateUser(uid);
    }

    public Task<Shift?> GetShiftByIdAsync(Guid shiftId) =>
        repo.GetShiftByIdAsync(shiftId);

    public Task<IReadOnlyList<Shift>> GetShiftsByRotaAsync(Guid rotaId) =>
        repo.GetShiftsByRotaAsync(rotaId);

    public (Instant Start, Instant End, ShiftPeriod Period) ResolveShiftTimes(Shift shift, EventSettings eventSettings)
    {
        var start = shift.GetAbsoluteStart(eventSettings);
        var end = shift.GetAbsoluteEnd(eventSettings);
        var period = shift.GetShiftPeriod(eventSettings);
        return (start, end, period);
    }


    /// <summary>Resolves (period, subPeriod) to inclusive day-offset bounds. subPeriod only narrows when period is Build.</summary>
    private static (int? MinDayOffset, int? MaxDayOffset) GetDayOffsetBounds(
        ShiftPeriod? period, BuildSubPeriod? subPeriod, EventSettings es)
    {
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

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            minDayOffset = start;
            // BoundsFor is half-open; repo bounds are inclusive — subtract a day.
            maxDayOffset = end - 1;
        }

        return (minDayOffset, maxDayOffset);
    }

    /// <summary>
    /// Builds the explicit list of day offsets covered by <paramref name="period"/> (all three when null).
    /// Iteration-list counterpart to <see cref="GetDayOffsetBounds"/>.
    /// </summary>
    private static List<int> BuildDayOffsetList(
        ShiftPeriod? period, BuildSubPeriod? subPeriod, EventSettings es)
    {
        var offsets = new List<int>();
        if (period is null or ShiftPeriod.Build)
            for (var d = es.BuildStartOffset; d < 0; d++) offsets.Add(d);
        if (period is null or ShiftPeriod.Event)
            for (var d = 0; d <= es.EventEndOffset; d++) offsets.Add(d);
        if (period is null or ShiftPeriod.Strike)
            for (var d = es.EventEndOffset + 1; d <= es.StrikeEndOffset; d++) offsets.Add(d);

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            offsets = offsets.Where(d => d >= start && d < end).ToList();
        }

        return offsets;
    }

    public async Task<IReadOnlyList<UrgentShift>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null,
        ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null)
    {
        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        var (minDayOffset, maxDayOffset) = GetDayOffsetBounds(period, subPeriod, es);

        // Date range overrides period bounds; defensively swap if start > end.
        if (startDate.HasValue || endDate.HasValue)
        {
            var s = startDate ?? endDate;
            var e = endDate ?? startDate;
            if (s.HasValue && e.HasValue && s.Value > e.Value)
            {
                (s, e) = (e, s);
            }
            minDayOffset = s.HasValue
                ? Period.Between(es.GateOpeningDate, s.Value, PeriodUnits.Days).Days
                : null;
            maxDayOffset = e.HasValue
                ? Period.Between(es.GateOpeningDate, e.Value, PeriodUnits.Days).Days
                : null;
        }

        var urgencyTeamIds = await ResolveDepartmentTeamIdsAsync(departmentId);
        var shifts = await repo.GetShiftsWithSignupsForUrgencyAsync(
            eventSettingsId, urgencyTeamIds, minDayOffset, maxDayOffset);

        // Resolve team names in one batch (no cross-domain Include).
        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIds);

        var now = clock.GetCurrentInstant();
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
        bool includeHidden = false, bool priorityOnly = false)
    {
        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        int? fromOffset = fromDate.HasValue
            ? Period.Between(es.GateOpeningDate, fromDate.Value, PeriodUnits.Days).Days
            : null;
        int? toOffset = toDate.HasValue
            ? Period.Between(es.GateOpeningDate, toDate.Value, PeriodUnits.Days).Days
            : null;

        var departmentTeamIds = await ResolveDepartmentTeamIdsAsync(departmentId);

        IReadOnlyList<Shift> shifts = await repo.GetShiftsWithSignupsForEventAsync(
            eventSettingsId, departmentTeamIds, includeAdminOnly, includeHidden,
            fromOffset, toOffset, includeRotaTags: true);

        // priorityOnly: rota is Important/Essential OR any sibling shift is understaffed (rota-wide test).
        if (priorityOnly)
        {
            var priorityRotaIds = shifts
                .GroupBy(s => s.RotaId)
                .Where(g =>
                    g.First().Rota.Priority is ShiftPriority.Important or ShiftPriority.Essential ||
                    g.Any(s => s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed) < s.MinVolunteers))
                .Select(g => g.Key)
                .ToHashSet();
            shifts = shifts.Where(s => priorityRotaIds.Contains(s.RotaId)).ToList();
        }

        // Cross-domain lookups via services.
        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIds);

        IReadOnlyDictionary<Guid, UserInfo>? userLookup = null;
        if (includeSignups)
        {
            var userIds = shifts
                .SelectMany(s => s.ShiftSignups)
                .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .Select(ss => ss.UserId)
                .Distinct()
                .ToList();
            if (userIds.Count > 0)
                userLookup = await UserService.GetUserInfosAsync(userIds);
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
                            UserInfo? user = null;
                            userLookup?.TryGetValue(ss.UserId, out user);
                            return (
                                ss.UserId,
                                DisplayName: user?.BurnerName ?? string.Empty,
                                ss.Status);
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

    // Mirrors the pie bucketing in GetDepartmentCoveragePiesAsync: filtering by a
    // department includes its non-promoted sub-teams (which roll up into the parent's
    // pie) but excludes promoted sub-teams (which have their own pie and filter).
    // Uses the warm in-process team cache (GetTeamsAsync) rather than a fresh DB scope.
    private async Task<IReadOnlyCollection<Guid>?> ResolveDepartmentTeamIdsAsync(Guid? departmentId)
    {
        if (!departmentId.HasValue) return null;
        var allTeams = await TeamService.GetTeamsAsync();
        var ids = new HashSet<Guid> { departmentId.Value };
        foreach (var team in allTeams.Values)
        {
            if (team.ParentTeamId == departmentId.Value && !team.IsPromotedToDirectory)
                ids.Add(team.Id);
        }
        return ids;
    }

    public double CalculateScore(Shift shift, int confirmedCount, EventSettings eventSettings)
    {
        var remainingSlots = Math.Max(0, shift.MaxVolunteers - confirmedCount);
        if (remainingSlots == 0) return 0;

        var priorityWeight = PriorityWeights.GetValueOrDefault(shift.Rota?.Priority ?? ShiftPriority.Normal, 1);
        var durationHours = shift.Duration.TotalHours;
        var understaffedMultiplier = confirmedCount < shift.MinVolunteers ? 2 : 1;

        // Proximity boost: 1 + 10/(1+daysUntilStart). Today→11x, tomorrow→6x, 7d→2.25x, 30d→1.32x.
        var now = clock.GetCurrentInstant();
        var shiftStart = shift.GetAbsoluteStart(eventSettings);
        var daysUntilStart = Math.Max(0, (shiftStart - now).TotalDays);
        var proximityBoost = 1.0 + (10.0 / (1.0 + daysUntilStart));

        return remainingSlots * priorityWeight * durationHours * understaffedMultiplier * proximityBoost;
    }

    /// <summary>Top-N with period diversity: one slot per non-Build period if eligible, rest from top scorers.</summary>
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


    public async Task<IReadOnlyList<DailyStaffingData>> GetStaffingDataAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null)
    {
        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        var tz = DateTimeZoneProviders.Tzdb[es.TimeZoneId];

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return [];

        var departmentTeamIds = await ResolveDepartmentTeamIdsAsync(departmentId);
        var shifts = await repo.GetShiftsForEventAsync(eventSettingsId, departmentTeamIds);

        // Need signup counts per shift (confirmed).
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var confirmedCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

        var results = new List<DailyStaffingData>();

        foreach (var dayOffset in dayOffsets)
        {
            var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
            var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
            var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
            var periodLabel = dayOffset < 0 ? "Set-up" : dayOffset <= es.EventEndOffset ? "Event" : "Strike";
            var dateLabel = dayDate.ToDisplayShiftDate();

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
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null)
    {
        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        var tz = DateTimeZoneProviders.Tzdb[es.TimeZoneId];

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return [];

        var departmentTeamIds = await ResolveDepartmentTeamIdsAsync(departmentId);
        var shifts = await repo.GetShiftsForEventAsync(eventSettingsId, departmentTeamIds);
        var results = new List<DailyStaffingHours>();

        foreach (var dayOffset in dayOffsets)
        {
            var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
            var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
            var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
            var dateLabel = dayDate.ToDisplayShiftDate();

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
                var hours = shift.IsAllDay
                    ? Duration.FromTicks(Shift.AllDayWindowEnd.TickOfDay - Shift.AllDayWindowStart.TickOfDay).TotalHours
                    : shift.Duration.TotalHours;
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
        Guid eventSettingsId, IReadOnlyCollection<Guid> teamIds)
    {
        if (teamIds.Count == 0) return null;

        var rotas = await repo.GetRotasWithShiftsAndSignupsAsync(eventSettingsId, teamIds.ToList());
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
        repo.GetTeamIdsWithShiftsInEventAsync(eventSettingsId, teamIds, ct);

    public async Task<IReadOnlyList<DepartmentCoveragePie>> GetDepartmentCoveragePiesAsync(
        Guid eventSettingsId,
        LocalDate? fromDate = null,
        LocalDate? toDate = null,
        CancellationToken ct = default)
    {
        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId, ct);
        if (es is null) return [];

        var teamIdsWithRotas = await repo.GetTeamIdsWithRotasInEventAsync(eventSettingsId, ct);
        if (teamIdsWithRotas.Count == 0) return [];

        // Brings rota-owning teams + parents in one lookup (avoids second hop for sub-team rotas).
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIdsWithRotas, ct);

        var allRotas = await repo.GetRotasWithShiftsAndSignupsAsync(
            eventSettingsId, teamIdsWithRotas.ToList(), ct);

        // Pie-eligible teams = top-level departments + promoted sub-teams.
        var pieTeams = teamLookup.Values.Where(t => t.IsInDirectory).ToList();
        var pieTeamIds = pieTeams.Select(t => t.Id).ToHashSet();

        var requested = new Dictionary<Guid, decimal>();
        var filled = new Dictionary<Guid, decimal>();

        foreach (var rota in allRotas.Where(r => r.IsVisibleToVolunteers))
        {
            if (!teamLookup.TryGetValue(rota.TeamId, out var rotaTeam)) continue;

            // Bucket: own pie if eligible; otherwise nearest ancestor's pie.
            Guid? bucketId = pieTeamIds.Contains(rotaTeam.Id)
                ? rotaTeam.Id
                : (rotaTeam.ParentTeamId is { } pid && pieTeamIds.Contains(pid) ? pid : null);
            if (bucketId is null) continue;

            foreach (var shift in rota.Shifts.Where(s => !s.AdminOnly))
            {
                if (fromDate is not null || toDate is not null)
                {
                    var shiftDate = es.GateOpeningDate.PlusDays(shift.DayOffset);
                    if (fromDate is { } from && shiftDate < from) continue;
                    if (toDate is { } to && shiftDate > to) continue;
                }

                var hours = shift.IsAllDay ? AllDayShiftHours : (decimal)shift.Duration.TotalHours;
                requested[bucketId.Value] = requested.GetValueOrDefault(bucketId.Value)
                    + hours * shift.MaxVolunteers;

                var confirmed = shift.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed);
                var capped = Math.Min(confirmed, shift.MaxVolunteers);
                filled[bucketId.Value] = filled.GetValueOrDefault(bucketId.Value) + hours * capped;
            }
        }

        // Natural name order; display-layer ordering (sub-team adjacent to parent) lives in the VM.
        return pieTeams
            .Where(t => requested.GetValueOrDefault(t.Id) > 0)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new DepartmentCoveragePie(
                TeamId: t.Id,
                TeamName: t.Name,
                TeamSlug: t.Slug,
                IsSubTeam: t.ParentTeamId is not null,
                ParentTeamId: t.ParentTeamId,
                ParentTeamName: t.ParentTeamId is { } pid && teamLookup.TryGetValue(pid, out var parent)
                    ? parent.Name
                    : null,
                RequestedHours: requested[t.Id],
                FilledHours: filled.GetValueOrDefault(t.Id)))
            .ToList();
    }

    public async Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId)
    {
        var teamIds = await repo.GetTeamIdsWithRotasInEventAsync(eventSettingsId);
        if (teamIds.Count == 0) return [];

        // Filter parents back out — only rota-owning teams belong in the department dropdown.
        var rotaOwningIds = teamIds.ToHashSet();
        var teams = await TeamService.GetByIdsWithParentsAsync(teamIds);
        return teams.Values
            .Where(t => rotaOwningIds.Contains(t.Id))
            .Select(t => (t.Id, t.Name))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }


    internal static string OverviewCacheKey(Guid eventId, ShiftPeriod? period) =>
        $"dashboard-overview:{eventId}:{period?.ToString() ?? "all"}";
    internal static string CoordinatorActivityCacheKey(Guid eventId, ShiftPeriod? period) =>
        $"dashboard-coordinator-activity:{eventId}:{period?.ToString() ?? "all"}";
    internal static string TrendsCacheKey(Guid eventId, TrendWindow window, ShiftPeriod? period) =>
        $"dashboard-trends:{eventId}:{window}:{period?.ToString() ?? "all"}";

    private void EvictDashboardCaches(Guid eventSettingsId)
    {
        var periods = new ShiftPeriod?[] { null }.Concat(Enum.GetValues<ShiftPeriod>().Cast<ShiftPeriod?>());
        foreach (var period in periods)
        {
            cache.Remove(OverviewCacheKey(eventSettingsId, period));
            cache.Remove(CoordinatorActivityCacheKey(eventSettingsId, period));
            foreach (var window in Enum.GetValues<TrendWindow>())
                cache.Remove(TrendsCacheKey(eventSettingsId, window, period));
        }
    }

    public async Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null)
    {
        // Sub-period results aren't cached (4× key fan-out); base period overview is.
        if (subPeriod is not null)
            return await ComputeDashboardOverviewAsync(eventSettingsId, period, subPeriod);

        var cached = await cache.GetOrCreateAsync(OverviewCacheKey(eventSettingsId, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await ComputeDashboardOverviewAsync(eventSettingsId, period, subPeriod: null);
        });
        return cached!;
    }

    private async Task<DashboardOverview> ComputeDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod)
    {
        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null)
            return EmptyOverview();

        var allShifts = await repo.GetVisibleShiftsForEventAsync(eventSettingsId);

        var shifts = period is null
            ? allShifts
            : allShifts.Where(s => s.GetShiftPeriod(es) == period.Value).ToList();

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            shifts = shifts.Where(s => s.DayOffset >= start && s.DayOffset < end).ToList();
        }

        var shiftIds = shifts.Select(s => s.Id).ToList();
        var confirmedCountsRo = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);
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

        var ticketOrders = await TicketQueryService.GetTicketOrdersAsync();
        var ticketHolders = ticketOrders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId.HasValue)
            .Select(o => o.MatchedUserId!.Value)
            .ToHashSet();

        var engagedUserIds = await repo.GetEngagedUserIdsForShiftsAsync(shiftIds);
        var engaged = engagedUserIds.ToHashSet();

        var ticketHoldersEngaged = engaged.Count(ticketHolders.Contains);
        var nonTicketSignups = engaged.Count(u => !ticketHolders.Contains(u));

        var staleThreshold = clock.GetCurrentInstant().Minus(Duration.FromDays(3));
        var stalePendingCount = await repo.GetStalePendingSignupCountAsync(shiftIds, staleThreshold);

        // Teams resolved via ITeamService — no cross-domain navigation.
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
        0, 0, 0, 0, new PeriodBreakdown(0, 0, 0), 0, 0, 0, 0, []);

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
                    // "Direct" points at the parent's own team page.
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

    public async Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null)
    {
        // Sub-period bypasses cache (4× key fan-out).
        if (subPeriod is not null)
            return await ComputeCoordinatorActivityAsync(eventSettingsId, period, subPeriod);

        var cached = await cache.GetOrCreateAsync(CoordinatorActivityCacheKey(eventSettingsId, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await ComputeCoordinatorActivityAsync(eventSettingsId, period, subPeriod: null);
        });
        return cached!;
    }

    private async Task<IReadOnlyList<CoordinatorActivityRow>> ComputeCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod)
    {
        int? minDayOffset = null;
        int? maxDayOffset = null;

        if (period is not null)
        {
            var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
            if (es is null) return [];
            (minDayOffset, maxDayOffset) = GetDayOffsetBounds(period, subPeriod, es);
        }

        var pendingCounts = await repo.GetPendingSignupCountsByTeamAsync(
            eventSettingsId, minDayOffset, maxDayOffset);

        if (pendingCounts.Count == 0)
            return [];

        // Walk up parents until fixed-point (GetByIdsWithParentsAsync gives one level per call).
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

        var teamsById = await TeamService.GetTeamsAsync();
        var coordsRaw = relevantTeamIds
            .Where(teamsById.ContainsKey)
            .SelectMany(id => teamsById[id].Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .Select(m => new TeamCoordinatorRef(id, m.UserId)))
            .ToList();
        var coordinatorUserIds = coordsRaw.Select(c => c.UserId).Distinct().ToList();

        var userLookup = await UserService.GetUserInfosAsync(coordinatorUserIds);

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
                            user?.LastLoginAt);
                    })
                    .OrderBy(c => c.LastLoginAt ?? Instant.MinValue)
                    .ToList());

        CoordinatorActivityRow BuildRow(Guid teamId)
        {
            var t = teamMeta[teamId];
            var ownPending = pendingCounts.GetValueOrDefault(teamId, 0);
            var coords = coordsByTeam.GetValueOrDefault(teamId, []);

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
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null)
    {
        // Sub-period bypasses cache (4× key fan-out).
        if (subPeriod is not null)
            return await ComputeDashboardTrendsAsync(eventSettingsId, window, period, subPeriod);

        var cached = await cache.GetOrCreateAsync(TrendsCacheKey(eventSettingsId, window, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await ComputeDashboardTrendsAsync(eventSettingsId, window, period, subPeriod: null);
        });
        return cached!;
    }

    private async Task<IReadOnlyList<DashboardTrendPoint>> ComputeDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period, BuildSubPeriod? subPeriod)
    {
        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;
        var now = clock.GetCurrentInstant();
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

        var (minDayOffset, maxDayOffset) = GetDayOffsetBounds(period, subPeriod, es);

        var signupsInWindow = await repo.GetSignupCreatedAtsInWindowAsync(
            eventSettingsId, startInstant, endInstant, minDayOffset, maxDayOffset);

        var ticketsInWindow = (await TicketQueryService.GetTicketOrdersAsync())
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid
                && o.PurchasedAt >= startInstant
                && o.PurchasedAt < endInstant)
            .Select(o => o.PurchasedAt)
            .ToList();
        var loginsInWindow = (await UserService.GetAllUserInfosAsync().ConfigureAwait(false))
            .Where(u => u.LastLoginAt >= startInstant && u.LastLoginAt < endInstant)
            .Select(u => u.LastLoginAt!.Value)
            .ToList();

        LocalDate ToLocalDate(Instant i) => i.InZone(tz).Date;

        var signupsByDay = signupsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
        var ticketsByDay = ticketsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
        var loginCountsByDay = loginsInWindow
            .GroupBy(ToLocalDate)
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
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null)
    {
        // Only meaningful for Build/Strike; Event planning has different day-over-day dynamics.
        if (period is not (ShiftPeriod.Build or ShiftPeriod.Strike))
            return [];

        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;

        // Caller restricts to Build/Strike up-front (early return above).
        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return [];

        var shifts = await repo.GetVisibleShiftsForEventAsync(eventSettingsId);
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var confirmedCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

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
            var dateLabel = dayDate.ToDisplayShiftDate();

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
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null)
    {
        var empty = new CoverageHeatmap(
            [],
            []);

        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return empty;

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return empty;

        var allShifts = await repo.GetVisibleShiftsForEventAsync(eventSettingsId);
        if (allShifts.Count == 0) return empty;

        var shiftIds = allShifts.Select(s => s.Id).ToList();
        var confirmedCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

        var teamIds = allShifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await TeamService.GetByIdsWithParentsAsync(teamIds);

        var days = dayOffsets
            .Select(off =>
            {
                var date = es.GateOpeningDate.PlusDays(off);
                var label = date.ToDisplayShiftDate();
                var dayPeriod = off < 0 ? ShiftPeriod.Build : off <= es.EventEndOffset ? ShiftPeriod.Event : ShiftPeriod.Strike;
                return new CoverageHeatmapDay(off, date, label, dayPeriod);
            })
            .ToList();

        // Display team: top-level → self; promoted subteam → self; non-promoted subteam → parent.
        Team? DisplayTeamFor(Shift s)
        {
            if (!teamLookup.TryGetValue(s.Rota.TeamId, out var team)) return null;
            if (team.ParentTeamId is null) return team;
            if (team.IsPromotedToDirectory) return team;
            return team.ParentTeamId is Guid pid && teamLookup.TryGetValue(pid, out var parent) ? parent : null;
        }

        // One row per directory team (top-level always; subteams only if IsPromotedToDirectory).
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

            // Promoted subteam rows carry parent's name as the department tag.
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
        var es = await repo.GetActiveEventSettingsAsync(ct);
        if (es is null) return (0, 0, 0d);

        var allShifts = await repo.GetVisibleShiftsForEventAsync(es.Id, ct);
        if (allShifts.Count == 0) return (0, 0, 0d);

        var shiftIds = allShifts.Select(s => s.Id).ToList();
        var confirmedCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIds, ct);

        var total = allShifts.Sum(s => s.MaxVolunteers);
        var filled = allShifts.Sum(s =>
            Math.Min(confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0, s.MaxVolunteers));

        var ratio = total == 0 ? 0d : (double)filled / total;
        return (filled, total, ratio);
    }

    public async Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null)
    {
        if (period is null) return [];

        var es = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (es is null) return [];

        var allShifts = await repo.GetVisibleShiftsForEventAsync(eventSettingsId);
        var periodShifts = allShifts.Where(s => s.GetShiftPeriod(es) == period.Value).ToList();

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            periodShifts = periodShifts.Where(s => s.DayOffset >= start && s.DayOffset < end).ToList();
        }

        var shiftIds = periodShifts.Select(s => s.Id).ToList();
        var confirmedCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIds);

        int FilledSlotsOn(Shift s)
        {
            var confirmed = confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0;
            return Math.Min(confirmed, s.MaxVolunteers);
        }

        // Key: (IsAllDay, whole-hour Duration). All-day shifts collapse into one bucket.
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


    public async Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null)
    {
        var tags = await repo.GetTagsAsync(query);
        return tags
            .Select(tag => new ShiftTagSummary(tag.Id, tag.Name))
            .ToList();
    }

    public async Task<ShiftTagSummary> GetOrCreateTagAsync(string name)
    {
        var trimmed = name.Trim();
        var existing = await repo.FindTagByNameAsync(trimmed);
        if (existing is not null) return new ShiftTagSummary(existing.Id, existing.Name);

        var tag = new ShiftTag
        {
            Id = Guid.NewGuid(),
            Name = trimmed
        };
        await repo.AddTagAsync(tag);
        return new ShiftTagSummary(tag.Id, tag.Name);
    }

    public async Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds)
    {
        await repo.SetRotaTagsAsync(rotaId, tagIds);
        viewInvalidator.InvalidateRota(rotaId);
    }

    public async Task<IReadOnlyList<ShiftTagPreferenceSummary>> GetVolunteerTagPreferencesAsync(Guid userId)
    {
        var preferences = await repo.GetVolunteerTagPreferencesAsync(userId);
        return preferences
            .Select(tag => new ShiftTagPreferenceSummary(tag.Id, tag.Name))
            .ToList();
    }

    public async Task SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds)
    {
        await repo.SetVolunteerTagPreferencesAsync(userId, tagIds);
        viewInvalidator.InvalidateUser(userId);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetPendingShiftSignupCountsByTeamAsync(
        Guid eventSettingsId,
        CancellationToken cancellationToken = default)
    {
        var activeEventId = await repo.GetActiveEventIdAsync(eventSettingsId, cancellationToken);
        if (activeEventId == Guid.Empty)
            return new Dictionary<Guid, int>();

        return await repo.GetPendingSignupCountsByTeamAsync(activeEventId, null, null, cancellationToken);
    }


    public async Task<VolunteerEventProfile> GetOrCreateShiftProfileAsync(Guid userId)
    {
        var existing = await repo.GetVolunteerEventProfileAsync(userId);
        if (existing is not null) return existing;

        var now = clock.GetCurrentInstant();
        var profile = new VolunteerEventProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await repo.AddVolunteerEventProfileAsync(profile);
        viewInvalidator.InvalidateUser(userId);
        return profile;
    }

    public async Task UpdateShiftProfileAsync(VolunteerEventProfile profile)
    {
        profile.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateVolunteerEventProfileAsync(profile);
        viewInvalidator.InvalidateUser(profile.UserId);
    }

    public async Task<VolunteerEventProfile?> GetShiftProfileAsync(Guid userId, bool includeMedical)
    {
        var profile = await repo.GetVolunteerEventProfileAsync(userId);

        if (profile is not null && !includeMedical)
        {
            profile.MedicalConditions = null;
        }

        return profile;
    }

    public async Task<int> DeleteShiftProfilesForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var deleted = await repo.DeleteVolunteerEventProfilesForUserAsync(userId, ct);
        viewInvalidator.InvalidateUser(userId);
        return deleted;
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await repo.ReassignProfilesAndTagPrefsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        viewInvalidator.InvalidateUser(sourceUserId);
        viewInvalidator.InvalidateUser(targetUserId);
    }
}
