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
    private readonly ShiftEventSettings shiftSettings = new(repo, auditLogService, clock);
    private readonly ShiftScheduling scheduling = new(repo, calendarResolver, viewInvalidator, clock);
    private readonly ShiftDeletion deletion = new(repo, auditLogService, viewInvalidator, logger);
    private readonly ShiftVolunteerProfiles volunteerProfiles = new(repo, viewInvalidator, clock, logger);
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

    public Task<ShiftEventKnobs?> GetKnobsAsync(Guid eventSettingsId, CancellationToken ct = default) =>
        shiftSettings.GetKnobsAsync(eventSettingsId, ct);

    public async Task SaveKnobsAsync(
        Guid eventSettingsId, bool isShiftBrowsingOpen, int? globalVolunteerCap, int reminderLeadTimeHours,
        BuildSubPeriodStarts subPeriods, Guid actorUserId)
    {
        try
        {
            await shiftSettings.SaveKnobsAsync(eventSettingsId, isShiftBrowsingOpen, globalVolunteerCap,
                reminderLeadTimeHours, subPeriods, actorUserId);
        }
        finally
        {
            EvictDashboardCaches(eventSettingsId);
            viewInvalidator.InvalidateAll();
        }
    }

    public async Task SetShiftBrowsingOpenAsync(Guid eventSettingsId, bool isOpen)
    {
        try
        {
            await shiftSettings.SetShiftBrowsingOpenAsync(eventSettingsId, isOpen);
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

        await shiftSettings.EnsureKnobsRowAsync(rota.EventSettingsId);

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
    public Task<ShiftDeletionResult> DeleteRotaAsync(Guid rotaId, Guid actorUserId) =>
        deletion.DeleteRotaAsync(rotaId, actorUserId);

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

    public Task<ShiftGenerationResult> CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput input) =>
        scheduling.CreateBuildStrikeShiftsAsync(input);

    public Task<ShiftGenerationResult> GenerateEventShiftsAsync(GenerateEventShiftsInput input) =>
        scheduling.GenerateEventShiftsAsync(input);

    public Task<ShiftMutationResult> CreateShiftAsync(CreateShiftInput input) =>
        scheduling.CreateShiftAsync(input);

    public Task<ShiftMutationResult> UpdateShiftAsync(UpdateShiftInput input) =>
        scheduling.UpdateShiftAsync(input);

    public Task<ShiftDeletionResult> DeleteShiftAsync(Guid shiftId, Guid actorUserId) =>
        deletion.DeleteShiftAsync(shiftId, actorUserId);

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

    public Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null, CancellationToken ct = default) =>
        volunteerProfiles.GetTagsAsync(query, ct);

    public Task<ShiftTagSummary> GetOrCreateTagAsync(string name) =>
        volunteerProfiles.GetOrCreateTagAsync(name);

    public Task<bool> SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds) =>
        volunteerProfiles.SetVolunteerTagPreferencesAsync(userId, tagIds);

    public async Task<IReadOnlyDictionary<Guid, int>> GetActivePendingShiftSignupCountsByTeamAsync(
        CancellationToken cancellationToken = default)
    {
        var eventSettings = await calendarResolver.GetActiveAsync(cancellationToken);
        if (eventSettings is null)
            return new Dictionary<Guid, int>();

        return await repo.GetPendingSignupCountsByTeamAsync(eventSettings.Id, null, null, cancellationToken);
    }

    public Task UpdateShiftProfileAsync(ShiftVolunteerProfileInfo profile) =>
        volunteerProfiles.UpdateShiftProfileAsync(profile);

    public Task<ShiftVolunteerProfileInfo?> GetShiftProfileAsync(Guid userId, CancellationToken ct = default) =>
        volunteerProfiles.GetShiftProfileAsync(userId, ct);

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

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        volunteerProfiles.ReassignAsync(sourceUserId, targetUserId, actorUserId, updatedAt, ct);

    public Task<PostEventStats?> GetPostEventStatsAsync(
        Guid eventSettingsId,
        CancellationToken ct = default) =>
        dashboardMetrics.GetPostEventStatsAsync(eventSettingsId, ct);
}
