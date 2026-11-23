using Humans.Camps.Contracts;
using Humans.Tickets.Contracts;
using Humans.Shifts.Helpers;
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
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Shifts.Data;
using Humans.Base.Caching;
using Humans.Users.Contracts;
using Humans.Permissions.Contracts;
using Humans.Base.Authorization;

using Humans.Shifts.Models;

namespace Humans.Shifts.Services;

/// <summary>
/// Owns rota, shift and volunteer-profile workflows; staffing reads use the cached rota graph.
/// </summary>
internal sealed class ShiftManagementService(
    IShiftManagementRepository repo,
    IAuditLogService auditLogService,
    IServiceProvider serviceProvider,
    EventCalendarResolver calendarResolver,
    IClock clock,
    ILogger<ShiftManagementService> logger) : IShiftManagement, IShiftRotaRows, IUserMerge
{

    private IShiftRotaRows RotaRows => serviceProvider.GetRequiredService<IShiftRotaRows>();
    private ISettingsService SettingsService => serviceProvider.GetRequiredService<ISettingsService>();
    private IPermissionService Permissions => serviceProvider.GetRequiredService<IPermissionService>();

    private async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamLookupWithParentsAsync(
        IEnumerable<Guid> teamIds, CancellationToken ct = default)
    {
        var teamsById = await TeamService.GetTeamsAsync(ct);
        return ShiftStaffingAggregation.IncludeParents(teamsById, teamIds);
    }

    public Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default) =>
        Permissions.HasAsync(userId, ShiftsPermissions.Approve, new Owner(TeamOwner.Kind, departmentTeamId), ct);

    public async Task<bool> CanManageOrApproveAnyShiftsAsync(Guid userId, CancellationToken ct = default) =>
        (await Permissions.GetOwnersAsync(userId, ShiftsPermissions.Manage, ct)).Count > 0
        || (await Permissions.GetOwnersAsync(userId, ShiftsPermissions.Approve, ct)).Count > 0;

    public async Task<ShiftEventKnobs?> GetKnobsAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        var entity = await repo.GetEventSettingsByIdAsync(eventSettingsId, ct);
        return entity is null
            ? null
            : new ShiftEventKnobs(entity.IsShiftBrowsingOpen);
    }

    public async Task SaveKnobsAsync(
        Guid eventSettingsId, bool isShiftBrowsingOpen, BuildSubPeriodStarts subPeriods, Guid actorUserId)
    {
        var entity = await EnsureKnobsRowAsync(eventSettingsId);
        entity.IsShiftBrowsingOpen = isShiftBrowsingOpen;
        SetSubPeriods(entity, subPeriods);
        entity.UpdatedAt = clock.GetCurrentInstant();
        await repo.SaveEventSettingsAsync(entity, EntityMutationMode.Update);
        await auditLogService.LogAsync(
            ShiftsAuditActions.ShiftSettingsUpdated, nameof(EventSettings), eventSettingsId,
            $"Shift settings saved: browsing {(isShiftBrowsingOpen ? "open" : "closed")}, "
            + $"build sub-periods start first crew {subPeriods.FirstCrew}, set-up week {subPeriods.SetupWeek}, "
            + $"pre-event week {subPeriods.PreEventWeek}, finishing weekend {subPeriods.FinishingWeekend}.",
            actorUserId);
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
        await repo.SaveRotaAsync(rota, EntityMutationMode.Add,
            tagIds: tagIds is { Count: > 0 } ? tagIds : null);
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
        await repo.SaveRotaAsync(rota, EntityMutationMode.Update, tagIds: input.TagIds);

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

        await repo.DeleteRotaCascadeAsync(rotaId);
        await AuditDeletedPendingSignupsAsync(removedSignups, actorUserId, $"rota '{rota.Name}' was deleted");
        return new ShiftDeletionResult();
    }

    public async Task<IReadOnlyList<ShiftAdminRota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId, CancellationToken ct = default) =>
        [.. (await RotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.TeamId == teamId && r.Rota.EventSettingsId == eventSettingsId)];

    public async Task<IReadOnlyList<ShiftAdminRota>> GetRotaRowsAsync(
        IReadOnlyCollection<Guid>? rotaIds = null, CancellationToken ct = default)
    {
        var rotas = await repo.GetRotaRowsAsync(rotaIds, ct);
        return rotas.Select(ToAdminRota).ToList();
    }

    private static ShiftAdminRota ToAdminRota(Rota rota) =>
        new(
            ToRotaInfo(rota),
            [.. rota.Shifts.Select(s => new ShiftAdminShift(
                ToShiftInfo(s),
                [.. s.ShiftSignups.Select(ss => new ShiftAdminSignup(
                    ss.Id, ss.UserId, ss.SignupBlockId, ss.Status, ss.CreatedAt))]))]);

    public async Task<ShiftSummary?> BuildSummaryAsync(
        BurnSettingsInfo activeEvent,
        string? teamSlug = null,
        Guid? rotaId = null,
        CancellationToken ct = default)
    {
        // Null scope: the controller returns 404.
        ShiftSummaryScope scope;
        IReadOnlyCollection<Guid>? filterTeamIds = null;
        Guid? filterRotaId = null;
        TeamInfo? team = null;
        RotaInfo? rota = null;
        var eventRotas = (await RotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == activeEvent.Id)
            .ToList();

        if (teamSlug is null)
        {
            scope = ShiftSummaryScope.Global;
        }
        else
        {
            team = await TeamService.GetTeamBySlugAsync(teamSlug, ct);
            if (team is null) return null;

            var teamSet = ShiftStaffingAggregation.ResolveDepartmentTeamIds(team.Id, await TeamService.GetTeamsAsync(ct));

            if (rotaId is null)
            {
                scope = ShiftSummaryScope.Team;
                filterTeamIds = teamSet;
            }
            else
            {
                rota = eventRotas.FirstOrDefault(r => r.Rota.Id == rotaId.Value)?.Rota;
                if (rota is null || !teamSet.Contains(rota.TeamId))
                {
                    return null;
                }

                scope = ShiftSummaryScope.Rota;
                filterRotaId = rotaId;
            }
        }

        // All-day rows store a 24h sentinel, so they count Shift.AllDayWindowHours instead.
        var totals = eventRotas
            .Where(r => filterRotaId is { } rid
                ? r.Rota.Id == rid
                : filterTeamIds is not { Count: > 0 } || filterTeamIds.Contains(r.Rota.TeamId))
            .SelectMany(r => r.Shifts.SelectMany(s => s.Signups
                .Where(su => su.Status == SignupStatus.Confirmed)
                .Select(su => (su.UserId, Hours: s.Shift.IsAllDay ? Shift.AllDayWindowHours : s.Shift.DurationHours))))
            .GroupBy(x => x.UserId)
            .Select(g => (UserId: g.Key, Hours: g.Sum(x => x.Hours), Count: g.Count()))
            .ToList();

        var userIds = totals.Select(t => t.UserId);
        var userInfos = await UserService.GetUserInfosAsync(userIds, ct);

        var campRoster = await CampService.GetCampsForEventAsync(activeEvent.Id, ct);

        var humans = totals
            .Select(t =>
            {
                var camp = CampUserInfo.Resolve(campRoster, activeEvent.Id, t.UserId).Season;
                var name = userInfos.TryGetValue(t.UserId, out var ui) ? ui.BurnerName : string.Empty;
                return new ShiftSummaryHumanRow(
                    t.UserId, name, camp?.CampId, camp?.Name, t.Hours, t.Count);
            })
            .ToList();

        var camps = BuildCampPivot(humans, campRoster, activeEvent.Id);

        IReadOnlyList<ShiftSummaryTeamLink> teamLinks = scope == ShiftSummaryScope.Global
            ? await BuildDepartmentLinksAsync([.. eventRotas.Select(r => r.Rota.TeamId).Distinct()], ct)
            : [];
        IReadOnlyList<ShiftSummaryRotaLink> rotaLinks = scope == ShiftSummaryScope.Team
            ? [.. eventRotas
                .Where(r => filterTeamIds!.Contains(r.Rota.TeamId))
                .Select(r => new ShiftSummaryRotaLink(r.Rota.Id, r.Rota.Name))]
            : [];

        return new ShiftSummary(
            scope, team?.Name, team?.Slug, rota?.Name, rota?.Id,
            humans, camps, teamLinks, rotaLinks);
    }

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

    public async Task<RotaInfo?> GetRotaAsync(Guid rotaId, CancellationToken cancellationToken = default) =>
        (await RotaRows.GetRotaRowsAsync([rotaId], cancellationToken)).SingleOrDefault()?.Rota;

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
                var shift = new Shift
                {
                    Id = Guid.NewGuid(),
                    RotaId = input.RotaId,
                    IsAllDay = false,
                    DayOffset = day,
                    StartTime = slot.StartTime,
                    Duration = ToShiftDuration(slot.DurationHours),
                    MinVolunteers = input.MinVolunteers,
                    MaxVolunteers = input.MaxVolunteers,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                if (GetShiftScheduleRefusal(shift.DayOffset, shift.StartTime, shift.Duration, shift.IsAllDay, es) is { } refusal)
                    return ShiftGenerationResult.Failure(refusal);
                toInsert.Add(shift);
            }
        }

        if (toInsert.Count > 0)
        {
            await repo.AddShiftsAsync(toInsert);
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
                input.DurationHours, input.IsAllDay) is { } refusal)
            return ShiftMutationResult.Failure(refusal);

        var now = clock.GetCurrentInstant();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = input.RotaId,
            Description = input.Description,
            DayOffset = input.DayOffset,
            StartTime = input.StartTime,
            Duration = ToShiftDuration(input.DurationHours),
            MinVolunteers = input.MinVolunteers,
            MaxVolunteers = input.MaxVolunteers,
            AdminOnly = input.AdminOnly,
            IsAllDay = input.IsAllDay,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (GetShiftScheduleRefusal(shift.DayOffset, shift.StartTime, shift.Duration, shift.IsAllDay, es) is { } scheduleRefusal)
            return ShiftMutationResult.Failure(scheduleRefusal);

        await repo.SaveShiftAsync(shift, EntityMutationMode.Add);
        return ShiftMutationResult.Success("Shift created.", shift.Id);
    }

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
                input.DurationHours, shift.IsAllDay) is { } refusal)
            return ShiftMutationResult.Failure(refusal);

        var duration = ToShiftDuration(input.DurationHours);
        if (GetShiftScheduleRefusal(input.DayOffset, input.StartTime, duration, shift.IsAllDay, es) is { } scheduleRefusal)
            return ShiftMutationResult.Failure(scheduleRefusal);

        shift.Description = input.Description;
        shift.DayOffset = input.DayOffset;
        shift.StartTime = input.StartTime;
        shift.Duration = duration;
        shift.MinVolunteers = input.MinVolunteers;
        shift.MaxVolunteers = input.MaxVolunteers;
        shift.AdminOnly = input.AdminOnly;
        shift.UpdatedAt = clock.GetCurrentInstant();

        await repo.SaveShiftAsync(shift, EntityMutationMode.Update);
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

        await repo.DeleteShiftCascadeAsync(shiftId);
        await AuditDeletedPendingSignupsAsync(removedSignups, actorUserId,
            $"shift in '{shift.Rota.Name}' on day {shift.DayOffset} was deleted");
        return new ShiftDeletionResult();
    }

    public async Task<ShiftWithRota?> GetShiftByIdAsync(Guid shiftId, CancellationToken ct = default) =>
        (await RotaRows.GetRotaRowsAsync(ct: ct))
            .SelectMany(r => r.Shifts.Where(s => s.Shift.Id == shiftId).Select(s => new ShiftWithRota(s.Shift, r.Rota)))
            .FirstOrDefault();

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

        var urgencyTeamIds = await ResolveDepartmentTeamIdsAsync(departmentId, ct);
        var shifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId, urgencyTeamIds, minDayOffset, maxDayOffset,
            includeRestricted ? ShiftEventQueryFlags.None : ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);

        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await GetTeamLookupWithParentsAsync(teamIds, ct);

        var now = clock.GetCurrentInstant();
        var urgentShifts = shifts
            .Where(s => s.Shift.GetAbsoluteEnd(es) > now)
            .Select(s =>
            {
                var confirmedCount = s.ConfirmedCount;
                var score = ShiftUrgencyRanking.CalculateScore(s.Shift, s.Rota.Priority, confirmedCount, es, clock);
                var remaining = Math.Max(0, s.Shift.MaxVolunteers - confirmedCount);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var t) ? t.Name : string.Empty;
                return ToUrgentShiftInfo(s.Shift, s.Rota, es, score, confirmedCount, remaining, teamName, []);
            })
            .Where(u => u.UrgencyScore > 0)
            .OrderByDescending(u => u.UrgencyScore)
            .ToList();

        if (limit.HasValue)
            return ShiftUrgencyRanking.ApplyPeriodDiverseLimit(urgentShifts, limit.Value);

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

        var shifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            query.EventSettingsId, departmentTeamIds, fromOffset, toOffset,
            (includeAdminOnly ? ShiftEventQueryFlags.None : ShiftEventQueryFlags.ExcludeAdminOnly)
            | (includeHidden ? ShiftEventQueryFlags.None : ShiftEventQueryFlags.ExcludeHiddenRotas)), ct);

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
                .SelectMany(s => s.Signups)
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
                var confirmedCount = s.ConfirmedCount;
                var score = ShiftUrgencyRanking.CalculateScore(s.Shift, s.Rota.Priority, confirmedCount, es, clock);
                var remaining = Math.Max(0, s.Shift.MaxVolunteers - confirmedCount);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var t) ? t.Name : string.Empty;
                var signups = includeSignups
                    ? s.Signups
                        .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                        .Select(ss =>
                        {
                            UserInfo? user = null;
                            userLookup?.TryGetValue(ss.UserId, out user);
                            return new ShiftSignupInfo(ss.UserId, user?.BurnerName ?? string.Empty, ss.Status);
                        })
                        .ToList()
                    : [];
                return ToUrgentShiftInfo(s.Shift, s.Rota, es, score, confirmedCount, remaining, teamName, signups)
                    with
                { EarlyEntrySignupsClosed = lockedTeams.Contains(s.Rota.TeamId) };
            })
            .ToList();
    }

    internal static ShiftInfo ToShiftInfo(Shift shift) =>
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

    internal static RotaInfo ToRotaInfo(Rota rota) =>
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

    public async Task<ShiftStaffingSnapshot> GetStaffingSnapshotAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return ShiftStaffingSnapshot.Empty;

        var tz = es.TimeZone;

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return ShiftStaffingSnapshot.Empty;

        var departmentTeamIds = departmentId.HasValue
            ? ShiftStaffingAggregation.ResolveDepartmentTeamIds(departmentId.Value, await TeamService.GetTeamsAsync(ct))
            : null;
        var shifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            departmentTeamIds), ct);

        var staffingData = new List<DailyStaffingData>();
        var staffingHours = new List<DailyStaffingHours>();

        foreach (var dayOffset in dayOffsets)
        {
            var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
            var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
            var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
            var dateLabel = dayDate.ToWeekdayDayMonth();

            var overlapping = shifts.Where(s => OverlapsDay(s, es, dayStart, dayEnd)).ToList();

            var periodLabel = dayOffset < 0 ? "Set-up" : dayOffset <= es.EventEndOffset ? "Event" : "Strike";
            var totalSlots = overlapping.Sum(s => s.Shift.MaxVolunteers);
            var minSlots = overlapping.Sum(s => s.Shift.MinVolunteers);
            var confirmedCount = overlapping.Sum(s => s.ConfirmedCount);

            var essentialHours = 0.0;
            var importantHours = 0.0;
            var normalHours = 0.0;

            foreach (var shift in overlapping)
            {
                var overlapStart = Instant.Max(dayStart, shift.Shift.GetAbsoluteStart(es));
                var overlapEnd = Instant.Min(dayEnd, shift.Shift.GetAbsoluteEnd(es));
                var totalHours = (overlapEnd - overlapStart).TotalHours * shift.Shift.MaxVolunteers;

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

            staffingData.Add(new DailyStaffingData(dayOffset, dateLabel, confirmedCount, totalSlots, minSlots, periodLabel));
            staffingHours.Add(new DailyStaffingHours(dayOffset, dateLabel, essentialHours, importantHours, normalHours));
        }

        return new ShiftStaffingSnapshot(staffingData, staffingHours);
    }

    public async Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
        Guid eventSettingsId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default)
    {
        if (teamIds.Count == 0) return null;

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(eventSettingsId, teamIds), ct);
        if (allShifts.Count == 0) return null;

        var allSignups = allShifts.SelectMany(s => s.Signups).ToList();

        return new ShiftsSummaryData(
            TotalSlots: allShifts.Sum(s => s.Shift.MaxVolunteers),
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
            TeamIdsWithShifts: allShifts.Select(s => s.Rota.TeamId).ToHashSet());
    }

    public async Task<IReadOnlyList<DepartmentCoveragePie>> GetDepartmentCoveragePiesAsync(
        Guid eventSettingsId,
        LocalDate? fromDate = null,
        LocalDate? toDate = null,
        CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var allRotas = (await RotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == eventSettingsId)
            .ToList();
        var teamIdsWithRotas = allRotas.Select(r => r.Rota.TeamId).Distinct().ToList();
        if (teamIdsWithRotas.Count == 0) return [];

        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIdsWithRotas);

        var pieTeams = teamLookup.Values.Where(IsInDirectory).ToList();
        var pieTeamIds = pieTeams.Select(t => t.Id).ToHashSet();

        var requested = new Dictionary<Guid, decimal>();
        var filled = new Dictionary<Guid, decimal>();

        foreach (var row in allRotas.Where(r => r.Rota.IsVisibleToVolunteers))
        {
            if (!teamLookup.TryGetValue(row.Rota.TeamId, out var rotaTeam)) continue;

            Guid? bucketId = pieTeamIds.Contains(rotaTeam.Id)
                ? rotaTeam.Id
                : (rotaTeam.ParentTeamId is { } pid && pieTeamIds.Contains(pid) ? pid : null);
            if (bucketId is null) continue;

            foreach (var (shift, confirmed) in row.Shifts.Where(s => !s.Shift.AdminOnly).Select(s => (s.Shift, s.ConfirmedCount)))
            {
                if (fromDate is not null || toDate is not null)
                {
                    var shiftDate = es.GateOpeningDate.PlusDays(shift.DayOffset);
                    if (fromDate is { } from && shiftDate < from) continue;
                    if (toDate is { } to && shiftDate > to) continue;
                }

                var hours = shift.IsAllDay ? AllDayShiftHours : (decimal)shift.DurationHours;
                requested[bucketId.Value] = requested.GetValueOrDefault(bucketId.Value)
                    + hours * shift.MaxVolunteers;

                var capped = Math.Min(confirmed, shift.MaxVolunteers);
                filled[bucketId.Value] = filled.GetValueOrDefault(bucketId.Value) + hours * capped;
            }
        }

        // Display order (sub-team beside parent) lives in the view model.
        return pieTeams
            .Where(t => requested.GetValueOrDefault(t.Id) > 0)
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
        Guid eventSettingsId, CancellationToken ct = default)
    {
        var teamIds = (await RotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == eventSettingsId)
            .Select(r => r.Rota.TeamId)
            .Distinct()
            .ToList();
        if (teamIds.Count == 0) return [];

        // Parents came along for the lookup; only rota-owning teams belong in the dropdown.
        var rotaOwningIds = teamIds.ToHashSet();
        var teams = await GetTeamLookupWithParentsAsync(teamIds, ct);
        return teams.Values
            .Where(t => rotaOwningIds.Contains(t.Id))
            .Select(t => (t.Id, t.Name))
            .ToList();
    }

    public async Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null)
            return new DashboardOverview(0, 0, 0, 0, new PeriodBreakdown(0, 0, 0), 0, 0, 0, 0, []);

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);

        var shifts = period is null
            ? allShifts
            : allShifts.Where(s => s.Shift.GetShiftPeriod(es) == period.Value).ToList();

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            shifts = shifts.Where(s => s.Shift.DayOffset >= start && s.Shift.DayOffset < end).ToList();
        }

        var aggregate = ShiftStaffingAggregation.AggregateShifts(shifts, es);
        var periodFillRates = new PeriodBreakdown(
            Pct(aggregate.Build), Pct(aggregate.Event), Pct(aggregate.Strike));

        var ticketOrders = await TicketQueryService.GetTicketOrdersAsync(ct);
        var ticketHolders = ticketOrders
            .Where(o => o.IsCurrentEvent && o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId.HasValue)
            .Select(o => o.MatchedUserId!.Value)
            .ToHashSet();

        var engaged = shifts
            .SelectMany(s => s.Signups)
            .Where(su => su.Status != SignupStatus.Cancelled)
            .Select(su => su.UserId)
            .ToHashSet();

        var ticketHoldersEngaged = engaged.Count(ticketHolders.Contains);
        var nonTicketSignups = engaged.Count(u => !ticketHolders.Contains(u));

        var staleThreshold = clock.GetCurrentInstant().Minus(Duration.FromDays(3));
        var stalePendingCount = shifts
            .SelectMany(s => s.Signups)
            .Count(su => su.Status == SignupStatus.Pending && su.CreatedAt < staleThreshold);

        var teamIdsOnRotas = shifts
            .Select(s => s.Rota.TeamId)
            .Distinct()
            .ToList();
        var teamsById = await TeamService.GetTeamsAsync(ct);
        var teamLookup = ShiftStaffingAggregation.IncludeParents(teamsById, teamIdsOnRotas);

        var departments = ShiftStaffingAggregation.BuildDepartmentRows(shifts, es, teamLookup);

        return new DashboardOverview(
            aggregate.Total, aggregate.Filled, aggregate.TotalSlots, aggregate.FilledSlots, periodFillRates,
            ticketHolders.Count, ticketHoldersEngaged, nonTicketSignups, stalePendingCount,
            departments);

        static double Pct(PeriodStaffing staffing) =>
            staffing.TotalSlots > 0 ? 100.0 * staffing.FilledSlots / staffing.TotalSlots : 0d;
    }

    public async Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        int? minDayOffset = null;
        int? maxDayOffset = null;

        if (period is not null)
        {
            var es = await calendarResolver.GetAsync(eventSettingsId, ct);
            if (es is null) return [];
            (minDayOffset, maxDayOffset) = BuildSubPeriodClassifier.GetDayOffsetBounds(period, subPeriod, es);
        }

        var pendingCounts = (await RotaRows.GetEventShiftsAsync(
                new ShiftEventQuery(eventSettingsId, MinDayOffset: minDayOffset, MaxDayOffset: maxDayOffset), ct))
            .PendingCountsByTeam();

        if (pendingCounts.Count == 0)
            return [];

        var teamsById = await TeamService.GetTeamsAsync(ct);
        var teamMeta = ShiftStaffingAggregation.IncludeParents(teamsById, pendingCounts.Keys);

        var relevantTeamIds = new HashSet<Guid>(pendingCounts.Keys.Where(teamMeta.ContainsKey));
        foreach (var id in relevantTeamIds.ToList())
        {
            var current = teamMeta.GetValueOrDefault(id);
            while (current?.ParentTeamId is { } parentId && teamMeta.ContainsKey(parentId))
            {
                if (!relevantTeamIds.Add(parentId))
                    break;
                current = teamMeta[parentId];
            }
        }

        var coordsRaw = relevantTeamIds
            .Where(teamsById.ContainsKey)
            .SelectMany(id => teamsById[id].Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .Select(m => (TeamId: id, m.UserId)))
            .ToList();
        var coordinatorUserIds = coordsRaw.Select(c => c.UserId);

        var userLookup = await UserService.GetUserInfosAsync(coordinatorUserIds, ct);

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
            .ToList();
    }

    public async Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // CreatedAt is Shifts-owned, not on the Settings calendar; TrendWindow.All starts there.
        var local = await repo.GetEventSettingsByIdAsync(eventSettingsId, ct);
        if (local is null) return [];
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var tz = es.TimeZone;
        var now = clock.GetCurrentInstant();
        var today = now.InZone(tz).Date;

        LocalDate start = window switch
        {
            TrendWindow.Last7Days => today.PlusDays(-6),
            TrendWindow.Last30Days => today.PlusDays(-29),
            TrendWindow.Last90Days => today.PlusDays(-89),
            TrendWindow.All => local.CreatedAt.InZone(tz).Date,
            _ => today.PlusDays(-29),
        };

        if (start > today) start = today;

        var startInstant = start.AtStartOfDayInZone(tz).ToInstant();
        var endInstant = today.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();

        var (minDayOffset, maxDayOffset) = BuildSubPeriodClassifier.GetDayOffsetBounds(period, subPeriod, es);

        var signupsInWindow = (await RotaRows.GetEventShiftsAsync(
                new ShiftEventQuery(eventSettingsId, MinDayOffset: minDayOffset, MaxDayOffset: maxDayOffset), ct))
            .SelectMany(s => s.Signups)
            .Select(su => su.CreatedAt)
            .Where(at => at >= startInstant && at < endInstant)
            .ToList();

        var ticketsInWindow = (await TicketQueryService.GetTicketOrdersAsync(ct))
            .Where(o => o.IsCurrentEvent && o.PaymentStatus == TicketPaymentStatus.Paid
                && o.PurchasedAt >= startInstant
                && o.PurchasedAt < endInstant)
            .Select(o => o.PurchasedAt)
            .ToList();
        var loginsInWindow = (await UserService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
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
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // Event days have different day-over-day dynamics.
        if (period is not (ShiftPeriod.Build or ShiftPeriod.Strike))
            return [];

        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var tz = es.TimeZone;

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return [];

        var shifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);

        var teamIdsOnRotas = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIdsOnRotas);

        (Guid Id, string Name) DeptOf(EventShift s)
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
            var dateLabel = dayDate.ToWeekdayDayMonth();

            var overlapping = shifts.Where(s => OverlapsDay(s, es, dayStart, dayEnd));

            var byDept = new Dictionary<Guid, (string Name, int Count)>();
            foreach (var s in overlapping)
            {
                var (deptId, deptName) = DeptOf(s);
                var confirmed = s.ConfirmedCount;
                if (confirmed == 0) continue;
                var cur = byDept.TryGetValue(deptId, out var existing) ? existing : (Name: deptName, Count: 0);
                byDept[deptId] = (cur.Name, cur.Count + confirmed);
            }

            var depts = byDept.Values
                .Select(v => new DepartmentDayCount(v.Name, v.Count))
                .ToList();

            results.Add(new DailyDepartmentStaffing(dayDate, dateLabel, depts));
        }

        return results;
    }

    public async Task<CoverageHeatmap> GetCoverageHeatmapAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        var empty = new CoverageHeatmap(
            [],
            []);

        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return empty;

        var tz = es.TimeZone;

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return empty;

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);
        if (allShifts.Count == 0) return empty;

        var teamIds = allShifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIds);

        var days = dayOffsets
            .Select(off =>
            {
                var date = es.GateOpeningDate.PlusDays(off);
                var label = date.ToWeekdayDayMonth();
                var dayPeriod = off < 0 ? ShiftPeriod.Build : off <= es.EventEndOffset ? ShiftPeriod.Event : ShiftPeriod.Strike;
                return new CoverageHeatmapDay(off, date, label, dayPeriod);
            })
            .ToList();

        // A non-promoted subteam displays as its parent.
        TeamInfo? DisplayTeamFor(EventShift s)
        {
            if (!teamLookup.TryGetValue(s.Rota.TeamId, out var team)) return null;
            if (team.ParentTeamId is null) return team;
            if (team.IsPromotedToDirectory) return team;
            return team.ParentTeamId is { } pid && teamLookup.TryGetValue(pid, out var parent) ? parent : null;
        }

        var shiftsByDisplayTeam = allShifts
            .Select(s => (Shift: s, Team: DisplayTeamFor(s)))
            .Where(x => x.Team is not null && IsInDirectory(x.Team))
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

                var overlapping = shifts.Where(s => OverlapsDay(s, es, dayStart, dayEnd)).ToList();

                var totalSlots = overlapping.Sum(s => s.Shift.MaxVolunteers);
                var filledSlots = overlapping.Sum(s => Math.Min(s.ConfirmedCount, s.Shift.MaxVolunteers));

                if (totalSlots > 0) teamHasAnyShift = true;
                cells.Add(new CoverageHeatmapCell(day.DayOffset, totalSlots, filledSlots));
            }

            if (!teamHasAnyShift) continue;

            var departmentName = team.ParentTeamId is { } pid && teamLookup.TryGetValue(pid, out var parentTeam)
                ? parentTeam.Name
                : team.Name;

            rows.Add(new CoverageHeatmapRotaRow(
                team.Id,
                team.Name,
                departmentName,
                cells));
        }

        return new CoverageHeatmap(days, rows);
    }

    public async Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default)
    {
        var es = await calendarResolver.GetActiveAsync(ct);
        if (es is null) return (0, 0, 0d);

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            es.Id,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);
        if (allShifts.Count == 0) return (0, 0, 0d);

        var total = allShifts.Sum(s => s.Shift.MaxVolunteers);
        var filled = allShifts.Sum(s => Math.Min(s.ConfirmedCount, s.Shift.MaxVolunteers));

        var ratio = total == 0 ? 0d : (double)filled / total;
        return (filled, total, ratio);
    }

    public async Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        if (period is null) return [];

        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);
        var periodShifts = allShifts.Where(s => s.Shift.GetShiftPeriod(es) == period.Value).ToList();

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            periodShifts = periodShifts.Where(s => s.Shift.DayOffset >= start && s.Shift.DayOffset < end).ToList();
        }

        // All-day shifts collapse into one bucket; others group by whole-hour duration.
        var grouped = periodShifts
            .GroupBy(s => (
                s.Shift.IsAllDay,
                Hours: s.Shift.IsAllDay ? 0 : (int)s.Shift.DurationHours))
            .Select(g => new ShiftDurationBreakdownRow(
                IsAllDay: g.Key.IsAllDay,
                DurationHours: g.Key.Hours,
                TotalSlots: g.Sum(s => s.Shift.MaxVolunteers),
                FilledSlots: g.Sum(s => Math.Min(s.ConfirmedCount, s.Shift.MaxVolunteers))))
            .ToList();

        return grouped;
    }

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
        return true;
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
    }

    public async Task<PostEventStats?> GetPostEventStatsAsync(
        Guid eventSettingsId,
        CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null)
            return null;

        // All shifts, so totals include those with zero signups.
        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);

        var teamIdsOnRotas = allShifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIdsOnRotas);

        string DeptNameOf(Guid deptId) =>
            teamLookup.TryGetValue(deptId, out var t) ? t.Name : string.Empty;

        string? DeptSlugOf(Guid deptId) =>
            teamLookup.TryGetValue(deptId, out var t) ? t.Slug : null;

        int totalConfirmed = 0;
        int totalNoShow = 0;
        int shiftsWithData = 0;

        var deptBuckets = new Dictionary<Guid, Dictionary<ShiftPeriod, (int Confirmed, int NoShow)>>();

        foreach (var shift in allShifts)
        {
            var deptId = ShiftStaffingAggregation.DepartmentIdOf(shift.Rota.TeamId, teamLookup);
            var period = shift.Shift.GetShiftPeriod(es);

            if (!deptBuckets.TryGetValue(deptId, out var periodBucket))
            {
                periodBucket = new Dictionary<ShiftPeriod, (int, int)>
                {
                    [ShiftPeriod.Build] = (0, 0),
                    [ShiftPeriod.Event] = (0, 0),
                    [ShiftPeriod.Strike] = (0, 0),
                };
                deptBuckets[deptId] = periodBucket;
            }

            var confirmed = shift.ConfirmedCount;
            var noShow = shift.Signups.Count(su => su.Status == SignupStatus.NoShow);
            totalConfirmed += confirmed;
            totalNoShow += noShow;
            if (confirmed + noShow > 0)
                shiftsWithData++;

            var cur = periodBucket[period];
            periodBucket[period] = (cur.Confirmed + confirmed, cur.NoShow + noShow);
        }

        PostEventPeriodRow ToPeriodRow(Dictionary<ShiftPeriod, (int Confirmed, int NoShow)> bucket, ShiftPeriod p)
        {
            var v = bucket[p];
            return new PostEventPeriodRow(v.Confirmed, v.NoShow);
        }

        var departments = deptBuckets
            .Select(kvp => new PostEventDepartmentRow(
                DepartmentId: kvp.Key,
                DepartmentName: DeptNameOf(kvp.Key),
                DepartmentSlug: DeptSlugOf(kvp.Key),
                TotalConfirmed: kvp.Value.Values.Sum(v => v.Confirmed),
                TotalNoShow: kvp.Value.Values.Sum(v => v.NoShow),
                Build: ToPeriodRow(kvp.Value, ShiftPeriod.Build),
                Event: ToPeriodRow(kvp.Value, ShiftPeriod.Event),
                Strike: ToPeriodRow(kvp.Value, ShiftPeriod.Strike)))
            .ToList();

        return new PostEventStats(
            TotalShifts: allShifts.Count,
            ShiftsWithData: shiftsWithData,
            TotalConfirmed: totalConfirmed,
            TotalNoShow: totalNoShow,
            Departments: departments);
    }

    // Lazy, to break the DI cycle with TeamService.
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();

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

    private static string? GetShiftInputRefusal(
        int minimum, int maximum, double? durationHours = null, bool isAllDay = false)
    {
        if (minimum < 0) return "MinVolunteers cannot be negative.";
        if (maximum < 1) return "MaxVolunteers must be at least one.";
        if (minimum > maximum) return "MinVolunteers cannot exceed MaxVolunteers.";
        if (durationHours is not { } hours) return null;
        if (!double.IsFinite(hours) || (!isAllDay && hours <= 0))
            return isAllDay
                ? "Shift duration must be a finite number of hours."
                : "Shift duration must be a positive finite number of hours.";

        Duration duration;
        try
        {
            duration = ToShiftDuration(hours);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Shift duration is outside the supported range.";
        }

        if (!isAllDay)
        {
            if (duration < Duration.FromSeconds(1))
                return "Shift duration must be at least one second.";
            // Hours arrive as doubles, so 2.3h is 8279.99999… seconds; tolerate that float noise.
            var seconds = hours * 3600;
            if (Math.Abs(seconds - Math.Round(seconds)) > 1e-6)
                return "Shift duration must contain a whole number of seconds.";
        }
        return null;
    }

    private static Duration ToShiftDuration(double hours) => Duration.FromSeconds(Math.Round(hours * 3600));

    private static string? GetShiftScheduleRefusal(
        int dayOffset, LocalTime startTime, Duration duration, bool isAllDay, IEventSettingsInfo settings)
    {
        const string refusal = "Shift start and end must fit within years 0001–9999 in UTC and the event timezone.";
        try
        {
            var zone = settings.TimeZone;
            var start = Shift.ResolveAbsoluteStart(dayOffset, startTime, isAllDay, settings);
            var end = Shift.ResolveAbsoluteEnd(dayOffset, startTime, duration, isAllDay, settings);
            return start.InUtc().Year < 1 || start.InZone(zone).Year < 1
                || end.InUtc().Year < 1 || end.InZone(zone).Year < 1
                ? refusal
                : null;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return refusal;
        }
    }

    private static (int Start, int End) GetRotaDayOffsetBounds(RotaPeriod period, BurnSettingsInfo es) =>
        period switch
        {
            RotaPeriod.Build => (es.BuildStartOffset, -1),
            RotaPeriod.Event => (0, es.EventEndOffset),
            RotaPeriod.Strike => (es.EventEndOffset + 1, es.StrikeEndOffset),
            _ => (es.BuildStartOffset, es.StrikeEndOffset)
        };

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

    private ITicketService TicketQueryService => serviceProvider.GetRequiredService<ITicketService>();

    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();

    private static readonly decimal AllDayShiftHours = (decimal)Shift.AllDayWindowHours;

    /// <summary>All three periods when <paramref name="period"/> is null.</summary>
    private static List<int> BuildDayOffsetList(
        ShiftPeriod? period, BuildSubPeriod? subPeriod, BurnSettingsInfo es)
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

    private static bool OverlapsDay(EventShift shift, BurnSettingsInfo calendar, Instant dayStart, Instant dayEnd)
    {
        var start = shift.Shift.GetAbsoluteStart(calendar);
        var end = shift.Shift.GetAbsoluteEnd(calendar);
        return start < dayEnd && end > dayStart;
    }

    private static bool IsInDirectory(TeamInfo team) =>
        team.ParentTeamId is null || team.IsPromotedToDirectory;

    private ICampService CampService => serviceProvider.GetRequiredService<ICampService>();

    // Left-joins the active-camp roster so camps with nobody in scope show as zero rows; campless humans get a
    // CampId-null bucket.
    private static IReadOnlyList<ShiftSummaryCampRow> BuildCampPivot(
        IReadOnlyList<ShiftSummaryHumanRow> humans, IReadOnlyList<CampInfo> roster, Guid eventSettingsId)
    {
        var byCamp = humans
            .Where(h => h.CampId is not null)
            .GroupBy(h => h.CampId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (
                    People: g.Count(),
                    Hours: g.Sum(h => h.Hours),
                    Count: g.Sum(h => h.Count),
                    Name: g.Select(h => h.CampName).FirstOrDefault(n => n is not null)));

        var rows = new List<ShiftSummaryCampRow>();
        var seen = new HashSet<Guid>();

        foreach (var camp in roster)
        {
            var season = camp.GetSeasonForEvent(eventSettingsId);
            if (season is not { Status: CampSeasonStatus.Active or CampSeasonStatus.Full })
                continue;

            seen.Add(camp.Id);
            rows.Add(byCamp.TryGetValue(camp.Id, out var hit)
                ? new ShiftSummaryCampRow(camp.Id, season.Name, hit.People, hit.Hours, hit.Count)
                : new ShiftSummaryCampRow(camp.Id, season.Name, 0, 0, 0));
        }

        // Camps the roster missed still get a row, so no hours silently vanish.
        foreach (var (campId, a) in byCamp)
            if (!seen.Contains(campId))
                rows.Add(new ShiftSummaryCampRow(campId, a.Name, a.People, a.Hours, a.Count));

        var campless = humans.Where(h => h.CampId is null).ToList();
        if (campless.Count > 0)
            rows.Add(new ShiftSummaryCampRow(
                null, null, campless.Count, campless.Sum(h => h.Hours), campless.Sum(h => h.Count)));

        return rows;
    }

    // A non-promoted sub-team folds into its parent; the inverse of the shared department expansion.
    private async Task<IReadOnlyList<ShiftSummaryTeamLink>> BuildDepartmentLinksAsync(
        IReadOnlyList<Guid> teamIdsWithRotas, CancellationToken ct)
    {
        if (teamIdsWithRotas.Count == 0) return [];

        var teams = await TeamService.GetTeamsAsync(ct);

        var departmentIds = new HashSet<Guid>();
        foreach (var teamId in teamIdsWithRotas)
        {
            if (!teams.TryGetValue(teamId, out var team)) continue;

            if (team.ParentTeamId is { } parentId
                && !team.IsPromotedToDirectory
                && teams.ContainsKey(parentId))
            {
                departmentIds.Add(parentId);
            }
            else
            {
                departmentIds.Add(teamId);
            }
        }

        return departmentIds
            .Select(id => teams[id])
            .Select(t => new ShiftSummaryTeamLink(t.Id, t.Name, t.Slug))
            .ToList();
    }

    private IRoleAssignmentService RoleAssignmentService => serviceProvider.GetRequiredService<IRoleAssignmentService>();

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
        ShiftInfo shift,
        RotaInfo rota,
        BurnSettingsInfo es,
        double score,
        int confirmedCount,
        int remainingSlots,
        string departmentName,
        IReadOnlyList<ShiftSignupInfo> signups) =>
        new(
            shift,
            rota,
            departmentName,
            es.GateOpeningDate.PlusDays(shift.DayOffset),
            shift.GetShiftPeriod(es),
            shift.GetAbsoluteStart(es),
            shift.GetAbsoluteEnd(es),
            score,
            confirmedCount,
            remainingSlots,
            signups);
}
