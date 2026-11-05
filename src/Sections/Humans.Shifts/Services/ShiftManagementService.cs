using System.Globalization;
using Humans.Auth.Contracts;
using Humans.Shifts.Services.Dtos;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Constants;
using Humans.Shifts.Domain;
using Humans.Settings.Contracts;
using Humans.Base.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Shifts.Data;
using Humans.Base.Caching;
using Humans.Users.Contracts;

using Humans.Shifts.Models;

namespace Humans.Shifts.Services;

/// <summary>
/// Caches the dashboard for 5 minutes. No grid cache: coordinator-team ids read through Teams' in-RAM model.
/// </summary>
internal sealed class ShiftManagementService(
    IShiftManagementRepository repo,
    IAuditLogService auditLogService,
    IAdminAuthorizationService adminAuthorization,
    IServiceProvider serviceProvider,
    IMemoryCache cache,
    CachingShiftViewService viewInvalidator,
    EventCalendarResolver calendarResolver,
    IClock clock,
    ILogger<ShiftManagementService> logger) : IShiftManagement, IUserMerge
{
    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromMinutes(5);
    private readonly ShiftDashboardMetrics dashboardMetrics = new(repo, calendarResolver, serviceProvider, clock);
    private readonly ShiftCoordinatorActivity coordinatorActivity = new(repo, calendarResolver, serviceProvider);
    private readonly ShiftCoverageReports coverageReports = new(repo, calendarResolver, serviceProvider);
    private readonly ShiftCampSummary campSummary = new(repo, serviceProvider);
    private const string OverallCoverageCacheKey = "dashboard-overall-coverage";

    // Lazy, to break the DI cycle with TeamService.
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IRoleAssignmentService RoleAssignmentService => serviceProvider.GetRequiredService<IRoleAssignmentService>();
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();
    private IShiftRowView RowView => serviceProvider.GetRequiredService<IShiftRowView>();
    private ISettingsService SettingsService => serviceProvider.GetRequiredService<ISettingsService>();

    private async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamLookupWithParentsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken ct = default)
    {
        var teamsById = await TeamService.GetTeamsAsync(ct);
        return ShiftStaffingAggregation.IncludeParents(teamsById, teamIds);
    }

    public async Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default)
    {
        var teamIds = await GetCoordinatorTeamIdsAsync(userId, ct);
        if (teamIds.Contains(departmentTeamId))
            return true;

        var team = await TeamService.GetTeamAsync(departmentTeamId, ct);
        return team?.ParentTeamId is not null && teamIds.Contains(team.ParentTeamId.Value);
    }

    public async Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default)
    {
        if (await RoleAssignmentService.HasActiveRoleAsync(userId, RoleNames.Admin, ct) ||
            await RoleAssignmentService.HasActiveRoleAsync(userId, ShiftsRoles.ShiftsNoInfo, ct) ||
            await RoleAssignmentService.HasActiveRoleAsync(userId, ShiftsRoles.ShiftsAdmin, ct))
            return true;

        return await IsDeptCoordinatorAsync(userId, departmentTeamId, ct);
    }

    // No cache of its own: Teams' singleton decorator answers from its in-RAM model, current on every team write.
    public Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId, CancellationToken ct = default) =>
        TeamService.GetUserCoordinatedTeamIdsAsync(userId, ct);

    public async Task<ShiftEventKnobs?> GetKnobsAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        var entity = await repo.GetEventSettingsByIdAsync(eventSettingsId, ct);
        return entity is null
            ? null
            : new ShiftEventKnobs(entity.IsShiftBrowsingOpen, entity.GlobalVolunteerCap, entity.ReminderLeadTimeHours);
    }

    public async Task SaveKnobsAsync(
        Guid eventSettingsId, bool isShiftBrowsingOpen, int? globalVolunteerCap, int reminderLeadTimeHours,
        BuildSubPeriodStarts subPeriods, Guid actorUserId)
    {
        var entity = await EnsureKnobsRowAsync(eventSettingsId);
        entity.IsShiftBrowsingOpen = isShiftBrowsingOpen;
        entity.GlobalVolunteerCap = globalVolunteerCap;
        entity.ReminderLeadTimeHours = reminderLeadTimeHours;
        SetSubPeriods(entity, subPeriods);
        entity.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveEventSettingsAsync(entity, EntityMutationMode.Update);
        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSettingsUpdated, nameof(EventSettings), eventSettingsId,
            $"Shift settings saved: browsing {(isShiftBrowsingOpen ? "open" : "closed")}, "
            + $"volunteer cap {globalVolunteerCap?.ToString(CultureInfo.InvariantCulture) ?? "none"}, reminder {reminderLeadTimeHours}h, "
            + $"build sub-periods start first crew {subPeriods.FirstCrew}, set-up week {subPeriods.SetupWeek}, "
            + $"pre-event week {subPeriods.PreEventWeek}, finishing weekend {subPeriods.FinishingWeekend}.",
            actorUserId);

        EvictDashboardCaches(eventSettingsId);
        viewInvalidator.InvalidateAll();
    }

    private static void SetSubPeriods(EventSettings entity, BuildSubPeriodStarts starts)
    {
        entity.SubPeriodFirstCrewStart = starts.FirstCrew;
        entity.SubPeriodSetupWeekStart = starts.SetupWeek;
        entity.SubPeriodPreEventWeekStart = starts.PreEventWeek;
        entity.SubPeriodFinishingWeekendStart = starts.FinishingWeekend;
    }

    /// <summary>
    /// Creates the knobs row on demand: "active event" is Settings' concept, and a Shifts row exists only once a
    /// rota or knob edit needs it. Callers that mutate the row save it themselves.
    /// </summary>
    private async Task<EventSettings> EnsureKnobsRowAsync(Guid eventSettingsId)
    {
        var existing = await repo.GetEventSettingsByIdAsync(eventSettingsId);
        if (existing is not null) return existing;

        var now = clock.GetCurrentInstant();
        var created = new EventSettings { Id = eventSettingsId, CreatedAt = now, UpdatedAt = now };
        SetSubPeriods(created, BuildSubPeriodStarts.Default);
        await repo.SaveEventSettingsAsync(created, EntityMutationMode.Add);
        return created;
    }

    public async Task SetShiftBrowsingOpenAsync(Guid eventSettingsId, bool isOpen)
    {
        var entity = await EnsureKnobsRowAsync(eventSettingsId);
        entity.IsShiftBrowsingOpen = isOpen;
        entity.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveEventSettingsAsync(entity, EntityMutationMode.Update);
    }

    public async Task<Guid> CreateRotaAsync(CreateRotaInput input, IReadOnlyList<Guid>? tagIds = null)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            TeamId = input.TeamId,
            EventSettingsId = input.EventSettingsId,
            Name = input.Name,
            Priority = input.Priority,
            Policy = input.Policy,
            Period = input.Period,
            IsVisibleToVolunteers = input.IsVisibleToVolunteers,
            CreatedAt = clock.GetCurrentInstant(),
        };
        await CreateRotaAsync(rota, tagIds);
        return rota.Id;
    }

    public async Task<int> DeleteEventAsync(
        Guid eventSettingsId,
        CancellationToken cancellationToken = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(cancellationToken);
        try
        {
            return await repo.DeleteEventCascadeAsync(eventSettingsId, cancellationToken);
        }
        finally
        {
            EvictDashboardCaches(eventSettingsId);
            viewInvalidator.InvalidateAll();
        }
    }

    public async Task CreateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null)
    {
        var team = await TeamService.GetTeamAsync(rota.TeamId);

        if (team is null)
            throw new InvalidOperationException("Team not found.");
        if (team.SystemTeamType != SystemTeamType.None)
            throw new InvalidOperationException("Rotas cannot be created on system teams.");

        // Any real Settings cycle will do; it need not be the active one.
        if (await calendarResolver.GetAsync(rota.EventSettingsId) is null)
            throw new InvalidOperationException("Event not found.");

        await EnsureKnobsRowAsync(rota.EventSettingsId);

        rota.UpdatedAt = clock.GetCurrentInstant();
        try
        {
            await repo.SaveRotaAsync(rota, EntityMutationMode.Add,
                tagIds: tagIds is { Count: > 0 } ? tagIds : null);
        }
        finally
        {
            viewInvalidator.InvalidateRota(rota.Id);
        }
    }

    public async Task<bool> UpdateRotaAsync(UpdateRotaInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null) return false;

        rota.Name = input.Name;
        rota.Description = input.Description;
        rota.PracticalInfo = input.PracticalInfo;
        rota.Priority = input.Priority;
        rota.Policy = input.Policy;
        rota.Period = input.Period;
        rota.IsVisibleToVolunteers = input.IsVisibleToVolunteers;
        rota.UpdatedAt = clock.GetCurrentInstant();
        try
        {
            await repo.SaveRotaAsync(rota, EntityMutationMode.Update, tagIds: input.TagIds);
        }
        finally
        {
            viewInvalidator.InvalidateRota(rota.Id);
        }

        return true;
    }

    public async Task<RotaMoveResult> MoveRotaToTeamAsync(MoveRotaInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null || rota.TeamId != input.SourceTeamId)
            return RotaMoveResult.Failure("Rota not found.");

        var targetTeam = await TeamService.GetTeamAsync(input.TargetTeamId);
        if (targetTeam is null)
            return RotaMoveResult.Failure("Target team not found.");
        if (targetTeam.ParentTeamId is not null)
            return RotaMoveResult.Failure("Rotas can only be moved to parent teams (departments).");
        if (targetTeam.SystemTeamType != SystemTeamType.None)
            return RotaMoveResult.Failure("Rotas cannot be moved to system teams.");
        if (rota.TeamId == input.TargetTeamId)
            return RotaMoveResult.Failure("Rota is already in this team.");

        var oldTeam = await TeamService.GetTeamAsync(rota.TeamId);
        var oldTeamName = oldTeam?.Name ?? "(unknown)";

        // Targeted write: only TeamId + UpdatedAt are marked modified so concurrent edits don't clobber.
        await repo.UpdateRotaTeamAssignmentAsync(
            rota.Id, input.TargetTeamId, clock.GetCurrentInstant());
        viewInvalidator.InvalidateRota(rota.Id);

        await auditLogService.LogAsync(
            ShiftsAuditActions.RotaMovedToTeam, nameof(Rota), rota.Id,
            $"Moved rota '{rota.Name}' from '{oldTeamName}' to '{targetTeam.Name}'",
            input.ActorUserId,
            // A persisted audit discriminator matched by exact equality, so a literal: Base cannot name the entity.
            relatedEntityId: input.TargetTeamId, relatedEntityType: "Team");

        return RotaMoveResult.Success($"Rota '{rota.Name}' moved to {targetTeam.Name}.", targetTeam.Slug);
    }
    public async Task<ShiftDeletionResult> DeleteRotaAsync(Guid rotaId, Guid actorUserId)
    {
        var rota = await repo.GetRotaAsync(rotaId, RotaReadShape.ShiftsWithSignups);
        if (rota is null) return RefuseDeletion(rotaId, "Shifts_Signup_RotaNotFound");

        var confirmedCount = rota.Shifts
            .SelectMany(s => s.ShiftSignups)
            .Count(d => d.Status == SignupStatus.Confirmed);

        if (confirmedCount > 0)
            return RefuseDeletion(rotaId, "Shifts_Delete_ConfirmedSignups", confirmedCount);

        // Captured before the cascade, for the audit after it commits.
        var removedSignups = rota.Shifts.SelectMany(s => s.ShiftSignups).ToList();
        var affectedUserIds = removedSignups.Select(s => s.UserId).Distinct().ToList();

        await repo.DeleteRotaCascadeAsync(rotaId);

        viewInvalidator.InvalidateRota(rotaId);
        foreach (var uid in affectedUserIds)
            viewInvalidator.InvalidateUser(uid);
        await AuditDeletedPendingSignupsAsync(removedSignups, actorUserId, $"rota '{rota.Name}' was deleted");
        return new ShiftDeletionResult();
    }

    public async Task<IReadOnlyList<ShiftAdminRota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId, CancellationToken ct = default)
    {
        var rotas = await repo.GetRotasAsync(eventSettingsId, [teamId], RotaReadShape.View, ct);
        return rotas
            .Select(r => new ShiftAdminRota(
                ToRotaInfo(r),
                [.. r.Shifts.Select(s => new ShiftAdminShift(
                    ToShiftInfo(s),
                    [.. s.ShiftSignups.Select(ss => new ShiftAdminSignup(
                        ss.Id, ss.UserId, ss.SignupBlockId, ss.Status, ss.CreatedAt))]))]))
            .ToList();
    }

    public Task<ShiftSummary?> BuildSummaryAsync(
        BurnSettingsInfo activeEvent,
        string? teamSlug = null,
        Guid? rotaId = null,
        CancellationToken ct = default) =>
        campSummary.BuildSummaryAsync(activeEvent, teamSlug, rotaId, ct);

    public async Task<IReadOnlyList<RotaSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return [];
        var trimmed = query.Trim();
        if (Guid.TryParse(trimmed, out var id))
        {
            var rota = await repo.GetRotaAsync(id, RotaReadShape.None, cancellationToken);
            return rota is null
                ? []
                : [new RotaSearchHit(rota.Id, rota.Name, StringSearchExtensions.ExactNameScore)];
        }

        var settings = await calendarResolver.GetActiveAsync(cancellationToken);
        if (settings is null) return [];

        var rotas = await repo.SearchVolunteerVisibleRotasAsync(
            trimmed, settings.Id, cancellationToken);

        return rotas
            .Select(r => new RotaSearchHit(r.Id, r.Name, r.Name.NameMatchScore(trimmed)))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.RotaId)
            .Take(max)
            .ToList();
    }

    public async Task<RotaInfo?> GetRotaAsync(Guid rotaId, CancellationToken cancellationToken = default)
    {
        var view = await RowView.GetRotaAsync(rotaId, cancellationToken);
        return view.Rota is null ? null : ToRotaInfo(view.Rota);
    }

    internal static LocalTime AllDayShiftStartTime => Shift.AllDayWindowStart;

    internal static LocalTime AllDayShiftEndTime => Shift.AllDayWindowEnd;

    public async Task<ShiftGenerationResult> CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
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

        foreach (var (minimum, maximum) in dailyStaffing.Values)
        {
            if (GetShiftInputRefusal(minimum, maximum) is { } refusal)
                return ShiftGenerationResult.Failure(refusal);
        }

        var es = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (es is null)
            return ShiftGenerationResult.Failure("Event calendar not configured.");

        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(rota.Period, es);
        foreach (var dayOffset in dailyStaffing.Keys)
        {
            if (dayOffset < periodStart || dayOffset > periodEnd)
                return ShiftGenerationResult.Failure($"Day offset {dayOffset} is outside the {rota.Period.ToString().ToLowerInvariant()} period ({periodStart} to {periodEnd})");
        }

        // Additive: days that already have shifts are skipped.
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
                // Ignored for all-day rows; midnight/24h is a neutral sentinel.
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
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftGenerationResult.Failure("Rota not found.");

        if (rota.Period != RotaPeriod.Event)
            return ShiftGenerationResult.Failure("Event shift generation is only for Event-period rotas.");

        var es = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (es is null)
            return ShiftGenerationResult.Failure("Event calendar not configured.");
        if (input.StartDayOffset < 0 ||
            input.EndDayOffset > es.EventEndOffset ||
            input.StartDayOffset > input.EndDayOffset)
            return ShiftGenerationResult.Failure("Shift dates must fall within the event period.");

        if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers) is { } staffingRefusal)
            return ShiftGenerationResult.Failure(staffingRefusal);

        if (input.TimeSlots.Count == 0)
            return ShiftGenerationResult.Failure("At least one time slot is required.");

        foreach (var slot in input.TimeSlots)
        {
            if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers, slot.DurationHours) is { } refusal)
                return ShiftGenerationResult.Failure(refusal);
        }

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
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftMutationResult.Failure("Rota not found.");

        var es = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (es is null)
            return ShiftMutationResult.Failure("Event calendar not configured.");
        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(rota.Period, es);
        if (input.DayOffset < periodStart || input.DayOffset > periodEnd)
            return ShiftMutationResult.Failure("Shift date must fall within the rota's period.");

        if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers,
                input.IsAllDay ? null : input.DurationHours) is { } refusal)
            return ShiftMutationResult.Failure(refusal);

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

        await repo.SaveShiftAsync(shift, EntityMutationMode.Add);
        viewInvalidator.InvalidateRota(input.RotaId);
        return ShiftMutationResult.Success("Shift created.", shift.Id);
    }

    private static string? GetShiftInputRefusal(int minimum, int maximum, double? durationHours = null)
    {
        if (minimum < 0) return "MinVolunteers cannot be negative.";
        if (maximum < 1) return "MaxVolunteers must be at least one.";
        if (minimum > maximum) return "MinVolunteers cannot exceed MaxVolunteers.";
        if (durationHours is { } hours && (!double.IsFinite(hours) || hours <= 0))
            return "Shift duration must be a positive finite number of hours.";
        return null;
    }

    private static (int Start, int End) GetRotaDayOffsetBounds(RotaPeriod period, BurnSettingsInfo es) =>
        period switch
        {
            RotaPeriod.Build => (es.BuildStartOffset, -1),
            RotaPeriod.Event => (0, es.EventEndOffset),
            RotaPeriod.Strike => (es.EventEndOffset + 1, es.StrikeEndOffset),
            _ => (es.BuildStartOffset, es.StrikeEndOffset)
        };

    public async Task<ShiftMutationResult> UpdateShiftAsync(UpdateShiftInput input)
    {
        var shift = await repo.GetShiftAsync(input.ShiftId, ShiftReadShape.Rota);
        if (shift is null || shift.Rota.TeamId != input.TeamId)
            return ShiftMutationResult.Failure("Shift not found.");

        var es = await calendarResolver.GetAsync(shift.Rota.EventSettingsId);
        if (es is null)
            return ShiftMutationResult.Failure("Event calendar not configured.");
        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(shift.Rota.Period, es);
        if (input.DayOffset < periodStart || input.DayOffset > periodEnd)
            return ShiftMutationResult.Failure("Shift date must fall within the rota's period.");

        if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers,
                shift.IsAllDay ? null : input.DurationHours) is { } refusal)
            return ShiftMutationResult.Failure(refusal);

        shift.Description = input.Description;
        shift.DayOffset = input.DayOffset;
        shift.StartTime = input.StartTime;
        shift.Duration = Duration.FromHours(input.DurationHours);
        shift.MinVolunteers = input.MinVolunteers;
        shift.MaxVolunteers = input.MaxVolunteers;
        shift.AdminOnly = input.AdminOnly;
        shift.UpdatedAt = clock.GetCurrentInstant();

        await repo.SaveShiftAsync(shift, EntityMutationMode.Update);
        viewInvalidator.InvalidateShift(shift.Id);
        return ShiftMutationResult.Success("Shift updated.", shift.Id);
    }

    public async Task<ShiftDeletionResult> DeleteShiftAsync(Guid shiftId, Guid actorUserId)
    {
        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Rota | ShiftReadShape.ShiftSignups);
        if (shift is null) return RefuseDeletion(shiftId, "Shifts_Signup_ShiftNotFound");

        var confirmedCount = shift.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
        if (confirmedCount > 0)
            return RefuseDeletion(shiftId, "Shifts_Delete_ConfirmedSignups", confirmedCount);

        var rotaId = shift.RotaId;
        var removedSignups = shift.ShiftSignups.ToList();
        var affectedUserIds = removedSignups.Select(s => s.UserId).Distinct().ToList();

        await repo.DeleteShiftCascadeAsync(shiftId);

        viewInvalidator.InvalidateShift(shiftId);
        viewInvalidator.InvalidateRota(rotaId);
        foreach (var uid in affectedUserIds)
            viewInvalidator.InvalidateUser(uid);
        await AuditDeletedPendingSignupsAsync(removedSignups, actorUserId,
            $"shift in '{shift.Rota.Name}' on day {shift.DayOffset} was deleted");
        return new ShiftDeletionResult();
    }

    private async Task AuditDeletedPendingSignupsAsync(
        IReadOnlyList<ShiftSignup> signups, Guid actorUserId, string reason)
    {
        foreach (var signup in signups.Where(s => s.Status == SignupStatus.Pending))
        {
            await auditLogService.LogAsync(ShiftsAuditActions.ShiftSignupCancelled, nameof(ShiftSignup), signup.Id,
                $"shift {signup.ShiftId}: {reason}", actorUserId,
                relatedEntityId: signup.UserId, relatedEntityType: AuditSubjectTypes.User);
        }
    }

    private ShiftDeletionResult RefuseDeletion(Guid entityId, string errorKey, int confirmedSignups = 0)
    {
        logger.LogWarning("Shift/rota deletion rejected for {EntityId}: {ErrorKey}, confirmed signups {ConfirmedSignups}",
            entityId, errorKey, confirmedSignups);
        return new ShiftDeletionResult(errorKey, confirmedSignups);
    }

    public async Task<ShiftWithRota?> GetShiftByIdAsync(Guid shiftId, CancellationToken ct = default)
    {
        var shift = await repo.GetShiftAsync(shiftId, ShiftReadShape.Rota, ct);
        return shift?.Rota is null ? null : new ShiftWithRota(ToShiftInfo(shift), ToRotaInfo(shift.Rota));
    }

    public async Task<IReadOnlyList<UrgentShiftInfo>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null,
        ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null,
        bool includeRestricted = false, CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var (minDayOffset, maxDayOffset) = BuildSubPeriodClassifier.GetDayOffsetBounds(period, subPeriod, es);

        // A reversed range is swapped. A single bound stays open-ended; do not mirror it onto the missing side.
        if (startDate.HasValue || endDate.HasValue)
        {
            var s = startDate;
            var e = endDate;
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

        var flags = ShiftEventQueryFlags.IncludeSignups;
        if (!includeRestricted)
            flags |= ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas;

        var urgencyTeamIds = await ResolveDepartmentTeamIdsAsync(departmentId, ct);
        var shifts = await repo.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            urgencyTeamIds,
            minDayOffset,
            maxDayOffset,
            flags), ct);

        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await GetTeamLookupWithParentsAsync(teamIds, ct);

        var now = clock.GetCurrentInstant();
        var urgentShifts = shifts
            .Where(s => s.GetAbsoluteEnd(es) > now)
            .Select(s =>
            {
                var confirmedCount = s.ShiftSignups.Count(d => d.Status == SignupStatus.Confirmed);
                var score = CalculateScore(s, confirmedCount, es);
                var remaining = Math.Max(0, s.MaxVolunteers - confirmedCount);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var t) ? t.Name : string.Empty;
                return ToUrgentShiftInfo(s, es, score, confirmedCount, remaining, teamName, []);
            })
            .Where(u => u.UrgencyScore > 0)
            .OrderByDescending(u => u.UrgencyScore)
            .ToList();

        if (limit.HasValue)
            return ApplyPeriodDiverseLimit(urgentShifts, limit.Value);

        return urgentShifts;
    }

    public async Task<IReadOnlyList<UrgentShiftInfo>> GetBrowseShiftsAsync(ShiftBrowseQuery query, CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(query.EventSettingsId, ct);
        if (es is null) return [];

        int? fromOffset = query.FromDate.HasValue
            ? Period.Between(es.GateOpeningDate, query.FromDate.Value, PeriodUnits.Days).Days
            : null;
        int? toOffset = query.ToDate.HasValue
            ? Period.Between(es.GateOpeningDate, query.ToDate.Value, PeriodUnits.Days).Days
            : null;

        var includeAdminOnly = query.Flags.HasFlag(ShiftBrowseQueryFlags.IncludeAdminOnly);
        var includeSignups = query.Flags.HasFlag(ShiftBrowseQueryFlags.IncludeSignups);
        var includeHidden = query.Flags.HasFlag(ShiftBrowseQueryFlags.IncludeHidden);

        var departmentTeamIds = await ResolveDepartmentTeamIdsAsync(query.DepartmentId, ct);

        var flags = ShiftEventQueryFlags.IncludeSignups | ShiftEventQueryFlags.IncludeRotaTags;
        if (!includeAdminOnly)
            flags |= ShiftEventQueryFlags.ExcludeAdminOnly;
        if (!includeHidden)
            flags |= ShiftEventQueryFlags.ExcludeHiddenRotas;

        IReadOnlyList<Shift> shifts = await repo.GetEventShiftsAsync(new ShiftEventQuery(
            query.EventSettingsId,
            departmentTeamIds,
            fromOffset,
            toOffset,
            flags), ct);

        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await GetTeamLookupWithParentsAsync(teamIds, ct);

        var lockedTeams = new HashSet<Guid>();
        if (es.IsEarlyEntryClosed(clock.GetCurrentInstant()))
            foreach (var teamId in teamIds)
                if (query.ViewerUserId is not { } viewerId || !await CanApproveSignupsAsync(viewerId, teamId, ct))
                    lockedTeams.Add(teamId);

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
                userLookup = await UserService.GetUserInfosAsync(userIds, ct);
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
                            return new ShiftSignupInfo(ss.UserId, user?.BurnerName ?? string.Empty, ss.Status);
                        })
                        .ToList()
                    : [];
                return ToUrgentShiftInfo(s, es, score, confirmedCount, remaining, teamName, signups)
                    with { EarlyEntrySignupsClosed = lockedTeams.Contains(s.Rota.TeamId) };
            })
            .OrderByDescending(u => u.UrgencyScore)
            .ToList();
    }

    // Mirrors the pie bucketing in GetDepartmentCoveragePiesAsync: non-promoted sub-teams roll into the parent,
    // promoted ones have their own pie and filter.
    private async Task<IReadOnlyCollection<Guid>?> ResolveDepartmentTeamIdsAsync(Guid? departmentId, CancellationToken ct = default)
    {
        if (!departmentId.HasValue) return null;
        var allTeams = await TeamService.GetTeamsAsync(ct);
        return ShiftStaffingAggregation.ResolveDepartmentTeamIds(departmentId.Value, allTeams);
    }

    /// <summary>
    /// Resolves the event-relative scalars here, where the event is in scope, so no consumer fetches the burn to
    /// render a row.
    /// </summary>
    private static UrgentShiftInfo ToUrgentShiftInfo(
        Shift shift,
        BurnSettingsInfo es,
        double score,
        int confirmedCount,
        int remainingSlots,
        string departmentName,
        IReadOnlyList<ShiftSignupInfo> signups) =>
        new(
            ToShiftInfo(shift),
            ToRotaInfo(shift.Rota),
            departmentName,
            es.GateOpeningDate.PlusDays(shift.DayOffset),
            shift.GetShiftPeriod(es),
            shift.GetAbsoluteStart(es),
            shift.GetAbsoluteEnd(es),
            score,
            confirmedCount,
            remainingSlots,
            signups);

    private static ShiftInfo ToShiftInfo(Shift shift) =>
        new(
            shift.Id,
            shift.RotaId,
            shift.Description,
            shift.DayOffset,
            shift.StartTime,
            shift.Duration.TotalHours,
            shift.MinVolunteers,
            shift.MaxVolunteers,
            shift.AdminOnly,
            shift.IsAllDay,
            shift.IsEarlyEntry);

    private static RotaInfo ToRotaInfo(Rota rota) =>
        new(
            rota.Id,
            rota.EventSettingsId,
            rota.TeamId,
            rota.Name,
            rota.Description,
            rota.PracticalInfo,
            rota.Priority,
            rota.Policy,
            rota.Period,
            rota.IsVisibleToVolunteers,
            rota.Tags.Select(t => new ShiftTagSummary(t.Id, t.Name)).ToList());

    internal double CalculateScore(Shift shift, int confirmedCount, IEventSettingsInfo eventSettings) =>
        ShiftUrgencyRanking.CalculateScore(shift, confirmedCount, eventSettings, clock);

    internal static List<UrgentShiftInfo> ApplyPeriodDiverseLimit(List<UrgentShiftInfo> rankedShifts, int limit) =>
        ShiftUrgencyRanking.ApplyPeriodDiverseLimit(rankedShifts, limit);

    public Task<ShiftStaffingSnapshot> GetStaffingSnapshotAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        coverageReports.GetStaffingSnapshotAsync(eventSettingsId, departmentId, period, subPeriod, ct);

    public async Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
        Guid eventSettingsId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0) return null;

        var rotas = await repo.GetRotasAsync(
            eventSettingsId,
            teamIds,
            RotaReadShape.ShiftsWithSignups,
            ct);
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
                .Count(),
            TeamIdsWithShifts: rotas
                .Where(r => r.Shifts.Count > 0)
                .Select(r => r.TeamId)
                .ToHashSet());
    }

    public Task<IReadOnlyList<DepartmentCoveragePie>> GetDepartmentCoveragePiesAsync(
        Guid eventSettingsId,
        LocalDate? fromDate = null,
        LocalDate? toDate = null,
        CancellationToken ct = default) =>
        coverageReports.GetDepartmentCoveragePiesAsync(eventSettingsId, fromDate, toDate, ct);

    public async Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        var teamIds = await repo.GetTeamIdsWithRotasInEventAsync(eventSettingsId, ct);
        if (teamIds.Count == 0) return [];

        // Parents came along for the lookup; only rota-owning teams belong in the dropdown.
        var rotaOwningIds = teamIds.ToHashSet();
        var teams = await GetTeamLookupWithParentsAsync(teamIds, ct);
        return teams.Values
            .Where(t => rotaOwningIds.Contains(t.Id))
            .Select(t => (t.Id, t.Name))
            .ToList();
    }

    internal static string OverviewCacheKey(Guid eventId, ShiftPeriod? period) =>
        $"dashboard-overview:{eventId}:{period?.ToString() ?? "all"}";
    internal static string CoordinatorActivityCacheKey(Guid eventId, ShiftPeriod? period) =>
        $"dashboard-coordinator-activity:{eventId}:{period?.ToString() ?? "all"}";
    internal static string TrendsCacheKey(Guid eventId, TrendWindow window, ShiftPeriod? period) =>
        $"dashboard-trends:{eventId}:{window}:{period?.ToString() ?? "all"}";

    public void InvalidateDashboardCaches(Guid eventSettingsId) => EvictDashboardCaches(eventSettingsId);

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
        cache.Remove(OverallCoverageCacheKey);
    }

    public async Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // Sub-periods would fan the cache key out 4x, so only the base period is cached.
        if (subPeriod is not null)
            return await dashboardMetrics.ComputeDashboardOverviewAsync(eventSettingsId, period, subPeriod, ct);

        var cached = await cache.GetOrCreateAsync(OverviewCacheKey(eventSettingsId, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await dashboardMetrics.ComputeDashboardOverviewAsync(eventSettingsId, period, subPeriod: null, ct);
        });
        return cached!;
    }

    public async Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // Sub-periods would fan the cache key out 4x, so only the base period is cached.
        if (subPeriod is not null)
            return await coordinatorActivity.ComputeCoordinatorActivityAsync(eventSettingsId, period, subPeriod, ct);

        var cached = await cache.GetOrCreateAsync(CoordinatorActivityCacheKey(eventSettingsId, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await coordinatorActivity.ComputeCoordinatorActivityAsync(eventSettingsId, period, subPeriod: null, ct);
        });
        return cached!;
    }

    public async Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // Sub-periods would fan the cache key out 4x, so only the base period is cached.
        if (subPeriod is not null)
            return await dashboardMetrics.ComputeDashboardTrendsAsync(eventSettingsId, window, period, subPeriod, ct);

        var cached = await cache.GetOrCreateAsync(TrendsCacheKey(eventSettingsId, window, period), async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await dashboardMetrics.ComputeDashboardTrendsAsync(eventSettingsId, window, period, subPeriod: null, ct);
        });
        return cached!;
    }

    public Task<IReadOnlyList<DailyDepartmentStaffing>> GetDailyDepartmentStaffingAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        coverageReports.GetDailyDepartmentStaffingAsync(eventSettingsId, period, subPeriod, ct);

    public Task<CoverageHeatmap> GetCoverageHeatmapAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        coverageReports.GetCoverageHeatmapAsync(eventSettingsId, period, subPeriod, ct);

    // Two /Admin components call this on every render; cached so one render costs one set of reads.
    public async Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default)
    {
        var cached = await cache.GetOrCreateAsync(OverallCoverageCacheKey, async entry =>
        {
            entry.SlidingExpiration = DashboardCacheTtl;
            return await coverageReports.ComputeOverallCoverageAsync(ct);
        });
        return cached;
    }

    public Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        coverageReports.GetShiftDurationBreakdownAsync(eventSettingsId, period, subPeriod, ct);

    public async Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null, CancellationToken ct = default)
    {
        var tags = await repo.GetTagsAsync(query, ct);
        return tags
            .Select(tag => new ShiftTagSummary(tag.Id, tag.Name))
            .ToList();
    }

    public async Task<ShiftTagSummary> GetOrCreateTagAsync(string name)
    {
        var trimmed = name.Trim();
        var tag = await repo.GetOrCreateTagAsync(trimmed);
        return new ShiftTagSummary(tag.Id, tag.Name);
    }

    public async Task<bool> SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds)
    {
        var knownIds = (await repo.GetTagsAsync()).Select(tag => tag.Id).ToHashSet();
        if (tagIds.Any(id => !knownIds.Contains(id)))
        {
            logger.LogWarning("Rejected unknown volunteer tag preferences for user {UserId}", userId);
            return false;
        }

        await repo.SetVolunteerTagPreferencesAsync(userId, tagIds);
        viewInvalidator.InvalidateUser(userId);
        return true;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetActivePendingShiftSignupCountsByTeamAsync(
        CancellationToken cancellationToken = default)
    {
        var eventSettings = await calendarResolver.GetActiveAsync(cancellationToken);
        if (eventSettings is null)
            return new Dictionary<Guid, int>();

        return await repo.GetPendingSignupCountsByTeamAsync(eventSettings.Id, null, null, cancellationToken);
    }

    public async Task UpdateShiftProfileAsync(ShiftVolunteerProfileInfo profile)
    {
        var now = clock.GetCurrentInstant();
        var existing = await repo.GetVolunteerEventProfileAsync(profile.UserId);
        var entity = existing ?? new VolunteerEventProfile
        {
            Id = Guid.NewGuid(),
            UserId = profile.UserId,
            CreatedAt = now
        };

        entity.Skills = [.. profile.Skills];
        entity.Quirks = [.. profile.Quirks];
        entity.Languages = [.. profile.Languages];
        entity.UpdatedAt = now;

        if (existing is null)
            await repo.AddVolunteerEventProfileAsync(entity);
        else
            await repo.UpdateVolunteerEventProfileAsync(entity);
        viewInvalidator.InvalidateUser(profile.UserId);
    }

    public async Task<ShiftVolunteerProfileInfo?> GetShiftProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await repo.GetVolunteerEventProfileAsync(userId, ct);
        return profile is null
            ? null
            : new ShiftVolunteerProfileInfo(profile.UserId, profile.Skills, profile.Quirks, profile.Languages);
    }

    public async Task<bool> HasQualifyingCantinaSignupAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        // Fails closed with no active event: no shifts means no cantina meals to plan for.
        var eventSettings = await calendarResolver.GetActiveAsync(ct);
        if (eventSettings is null)
            return false;

        var now = clock.GetCurrentInstant();
        var signups = await repo.GetForUsersAsync([userId], eventSettings.Id, ct);

        return signups.Any(s =>
            s.Shift is not null
            && (s.Status == SignupStatus.Pending || s.Status == SignupStatus.Confirmed)
            && s.Shift.QualifiesForCantinaMeal()
            && s.Shift.GetAbsoluteEnd(eventSettings) > now);
    }

    public Task<IReadOnlyList<Guid>> GetOnSiteUserIdsForDayAsync(
        Guid eventSettingsId, int dayOffset, CancellationToken ct = default) =>
        repo.GetUserIdsForDayAsync(
            eventSettingsId,
            dayOffset,
            ShiftDayUserStatusScope.ConfirmedOnly,
            ct);

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await repo.ReassignProfilesAndTagPrefsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        viewInvalidator.InvalidateUser(sourceUserId);
        viewInvalidator.InvalidateUser(targetUserId);
    }

    public Task<PostEventStats?> GetPostEventStatsAsync(
        Guid eventSettingsId,
        CancellationToken ct = default) =>
        dashboardMetrics.GetPostEventStatsAsync(eventSettingsId, ct);
}
