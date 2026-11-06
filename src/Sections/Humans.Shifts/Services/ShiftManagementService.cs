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
    private readonly ShiftBrowseReader browseReader = new(repo, calendarResolver, serviceProvider, clock);
    private const string OverallCoverageCacheKey = "dashboard-overall-coverage";

    // Lazy, to break the DI cycle with TeamService.
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IShiftRowView RowView => serviceProvider.GetRequiredService<IShiftRowView>();
    private ISettingsService SettingsService => serviceProvider.GetRequiredService<ISettingsService>();

    private Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamLookupWithParentsAsync(
        IEnumerable<Guid> teamIds, CancellationToken ct = default) =>
        browseReader.GetTeamLookupWithParentsAsync(teamIds, ct);

    public Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default) =>
        browseReader.IsDeptCoordinatorAsync(userId, departmentTeamId, ct);

    public Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default) =>
        browseReader.CanApproveSignupsAsync(userId, departmentTeamId, ct);

    public Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId, CancellationToken ct = default) =>
        browseReader.GetCoordinatorTeamIdsAsync(userId, ct);

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
        try
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
        }
        finally
        {
            EvictDashboardCaches(eventSettingsId);
            viewInvalidator.InvalidateAll();
        }
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
        try
        {
            var entity = await EnsureKnobsRowAsync(eventSettingsId);
            entity.IsShiftBrowsingOpen = isOpen;
            entity.UpdatedAt = clock.GetCurrentInstant();
            await repo.SaveEventSettingsAsync(entity, EntityMutationMode.Update);
        }
        finally
        {
            viewInvalidator.InvalidateAll();
        }
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
        try
        {
            await repo.UpdateRotaTeamAssignmentAsync(
                rota.Id, input.TargetTeamId, clock.GetCurrentInstant());
        }
        finally
        {
            viewInvalidator.InvalidateRota(rota.Id);
        }

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

        try
        {
            await repo.DeleteRotaCascadeAsync(rotaId);
        }
        finally
        {
            viewInvalidator.InvalidateRota(rotaId);
            foreach (var uid in affectedUserIds)
                viewInvalidator.InvalidateUser(uid);
        }
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
            try
            {
                await repo.AddShiftsAsync(toInsert);
            }
            finally
            {
                viewInvalidator.InvalidateRota(input.RotaId);
            }
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
            try
            {
                await repo.AddShiftsAsync(toInsert);
            }
            finally
            {
                viewInvalidator.InvalidateRota(input.RotaId);
            }
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

        try
        {
            await repo.SaveShiftAsync(shift, EntityMutationMode.Add);
        }
        finally
        {
            viewInvalidator.InvalidateRota(input.RotaId);
        }
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

        try
        {
            await repo.SaveShiftAsync(shift, EntityMutationMode.Update);
        }
        finally
        {
            viewInvalidator.InvalidateShift(shift.Id);
        }
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

        try
        {
            await repo.DeleteShiftCascadeAsync(shiftId);
        }
        finally
        {
            viewInvalidator.InvalidateShift(shiftId);
            viewInvalidator.InvalidateRota(rotaId);
            foreach (var uid in affectedUserIds)
                viewInvalidator.InvalidateUser(uid);
        }
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

    public Task<IReadOnlyList<UrgentShiftInfo>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null,
        ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null,
        bool includeRestricted = false, CancellationToken ct = default) =>
        browseReader.GetUrgentShiftsAsync(eventSettingsId, limit, departmentId, startDate, endDate,
            period, subPeriod, includeRestricted, ct);

    public Task<IReadOnlyList<UrgentShiftInfo>> GetBrowseShiftsAsync(ShiftBrowseQuery query, CancellationToken ct = default) =>
        browseReader.GetBrowseShiftsAsync(query, ct);

    private static ShiftInfo ToShiftInfo(Shift shift) => ShiftBrowseReader.ToShiftInfo(shift);

    private static RotaInfo ToRotaInfo(Rota rota) => ShiftBrowseReader.ToRotaInfo(rota);

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

        try
        {
            await repo.SetVolunteerTagPreferencesAsync(userId, tagIds);
        }
        finally
        {
            viewInvalidator.InvalidateUser(userId);
        }
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

        try
        {
            if (existing is null)
                await repo.AddVolunteerEventProfileAsync(entity);
            else
                await repo.UpdateVolunteerEventProfileAsync(entity);
        }
        finally
        {
            viewInvalidator.InvalidateUser(profile.UserId);
        }
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
        try
        {
            await repo.ReassignProfilesAndTagPrefsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        }
        finally
        {
            viewInvalidator.InvalidateUser(sourceUserId);
            viewInvalidator.InvalidateUser(targetUserId);
        }
    }

    public Task<PostEventStats?> GetPostEventStatsAsync(
        Guid eventSettingsId,
        CancellationToken ct = default) =>
        dashboardMetrics.GetPostEventStatsAsync(eventSettingsId, ct);
}
