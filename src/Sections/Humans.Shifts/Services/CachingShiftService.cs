using Humans.Base.Caching;
using Humans.Base.Threading;
using Humans.Gdpr.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Shifts.Services;

/// <summary>
/// Holds every rota with its shifts and signups, across all events, and refreshes the touched rotas after each write.
/// Reads still go to the inner services; they move onto <see cref="IShiftRotaRows"/> area by area.
/// </summary>
internal sealed class CachingShiftService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingShiftService> logger)
    : TrackedCache<Guid, ShiftAdminRota>("Shifts.Rota", warmOnStartup: true, logger),
    IShiftRotaRows, IShiftManagement, IShiftSignupService, IUserMerge, IUserDataContributor
{
    /// <summary>DI key for the undecorated inner services.</summary>
    public const string InnerServiceKey = "shift-inner";

    // The one lock. Held only while reloading rotas and storing them, and for the whole warmup, so a refresh never
    // lands in the middle of a warmup and a warmup never reads rows a refresh is about to replace.
    private readonly TrackedLock _refreshLock = new("Shifts.RotaRefresh");

    // ── Rows ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ShiftAdminRota>> GetRotaRowsAsync(
        IReadOnlyCollection<Guid>? rotaIds = null, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        if (rotaIds is null) return [.. Values];
        var rows = new List<ShiftAdminRota>(rotaIds.Count);
        foreach (var id in rotaIds)
            if (TryGet(id, out var row)) rows.Add(row);
        return rows;
    }

    protected override async Task WarmAllAsync(Action<Guid, ShiftAdminRota> publish, CancellationToken ct)
    {
        using var _ = await _refreshLock.AcquireAsync(logger, ct);
        foreach (var row in await RowsAsync(rows => rows.GetRotaRowsAsync(null, ct)))
            publish(row.Rota.Id, row);
    }

    /// <summary>
    /// Reloads the rotas <paramref name="affected"/> picks from the cache (null: every rota) and stores them.
    /// Never throws: a failed reload empties the cache, and the next read warms it again.
    /// </summary>
    private async Task RefreshAsync(Func<IEnumerable<Guid>?> affected)
    {
        try
        {
            using var _ = await _refreshLock.AcquireAsync(logger);
            var ids = affected()?.ToHashSet();
            if (ids is { Count: 0 }) return;

            var rows = await RowsAsync(source => source.GetRotaRowsAsync(ids, CancellationToken.None));
            foreach (var row in rows)
                Replace(row.Rota.Id, row);

            var gone = (ids ?? [.. AsReadOnlyDictionary.Keys]).Except(rows.Select(r => r.Rota.Id));
            foreach (var id in gone)
                DeleteKey(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Shift rota refresh failed; emptying the cache so the next read reloads it");
            Clear();
        }
    }

    private async Task<T> WriteAsync<T>(Func<Task<T>> write, Func<IEnumerable<Guid>?> affected)
    {
        try
        {
            return await write();
        }
        finally
        {
            await RefreshAsync(affected);
        }
    }

    private Task WriteAsync(Func<Task> write, Func<IEnumerable<Guid>?> affected) =>
        WriteAsync(async () =>
        {
            await write();
            return true;
        }, affected);

    private static IEnumerable<Guid>? AllRotas() => null;

    private IEnumerable<Guid> RotaOfShift(Guid shiftId) =>
        RotasWhere(r => r.Shifts.Any(s => s.Shift.Id == shiftId));

    private IEnumerable<Guid> RotaOfSignup(Guid signupId) =>
        RotasWhere(r => r.Shifts.Any(s => s.Signups.Any(x => x.Id == signupId)));

    private IEnumerable<Guid> RotaOfBlock(Guid blockId) =>
        RotasWhere(r => r.Shifts.Any(s => s.Signups.Any(x => (x.SignupBlockId ?? x.Id) == blockId)));

    private IEnumerable<Guid> RotasWhere(Func<ShiftAdminRota, bool> match) =>
        Values.Where(match).Select(r => r.Rota.Id);

    // ── Rota and shift writes ───────────────────────────────────────────

    public async Task<Guid> CreateRotaAsync(CreateRotaInput input, IReadOnlyList<Guid>? tagIds = null)
    {
        Guid? created = null;
        return await WriteAsync(
            async () => (created = await MgmtAsync(m => m.CreateRotaAsync(input, tagIds))).Value,
            () => created is { } id ? [id] : AllRotas());
    }

    public Task CreateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null) =>
        WriteAsync(() => MgmtAsync(m => m.CreateRotaAsync(rota, tagIds)), () => [rota.Id]);

    public Task<bool> UpdateRotaAsync(UpdateRotaInput input) =>
        WriteAsync(() => MgmtAsync(m => m.UpdateRotaAsync(input)), () => [input.RotaId]);

    public Task<RotaMoveResult> MoveRotaToTeamAsync(MoveRotaInput input) =>
        WriteAsync(() => MgmtAsync(m => m.MoveRotaToTeamAsync(input)), () => [input.RotaId]);

    public Task<ShiftDeletionResult> DeleteRotaAsync(Guid rotaId, Guid actorUserId) =>
        WriteAsync(() => MgmtAsync(m => m.DeleteRotaAsync(rotaId, actorUserId)), () => [rotaId]);

    public Task<int> DeleteEventAsync(Guid eventSettingsId, CancellationToken cancellationToken = default) =>
        WriteAsync(() => MgmtAsync(m => m.DeleteEventAsync(eventSettingsId, cancellationToken)), AllRotas);

    public Task<ShiftGenerationResult> CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput input) =>
        WriteAsync(() => MgmtAsync(m => m.CreateBuildStrikeShiftsAsync(input)), () => [input.RotaId]);

    public Task<ShiftGenerationResult> GenerateEventShiftsAsync(GenerateEventShiftsInput input) =>
        WriteAsync(() => MgmtAsync(m => m.GenerateEventShiftsAsync(input)), () => [input.RotaId]);

    public Task<ShiftMutationResult> CreateShiftAsync(CreateShiftInput input) =>
        WriteAsync(() => MgmtAsync(m => m.CreateShiftAsync(input)), () => [input.RotaId]);

    public Task<ShiftMutationResult> UpdateShiftAsync(UpdateShiftInput input) =>
        WriteAsync(() => MgmtAsync(m => m.UpdateShiftAsync(input)), () => RotaOfShift(input.ShiftId));

    public Task<ShiftDeletionResult> DeleteShiftAsync(Guid shiftId, Guid actorUserId) =>
        WriteAsync(() => MgmtAsync(m => m.DeleteShiftAsync(shiftId, actorUserId)), () => RotaOfShift(shiftId));

    // ── Signup writes ───────────────────────────────────────────────────

    public Task<SignupResult> SignUpAsync(
        Guid userId, Guid shiftId, Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None) =>
        WriteAsync(() => SignupsAsync(s => s.SignUpAsync(userId, shiftId, actorUserId, flags)), () => RotaOfShift(shiftId));

    public Task<SignupResult> SignUpRangeAsync(
        Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None) =>
        WriteAsync(
            () => SignupsAsync(s => s.SignUpRangeAsync(userId, rotaId, startDayOffset, endDayOffset, actorUserId, flags)),
            () => [rotaId]);

    public Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId) =>
        WriteAsync(() => SignupsAsync(s => s.VoluntellAsync(userId, shiftId, enrollerUserId)), () => RotaOfShift(shiftId));

    public Task<SignupResult> VoluntellRangeAsync(
        Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId) =>
        WriteAsync(
            () => SignupsAsync(s => s.VoluntellRangeAsync(userId, rotaId, startDayOffset, endDayOffset, enrollerUserId)),
            () => [rotaId]);

    public Task<ToggleDaySignupOutcome> ToggleDayAsync(
        Guid userId, Guid shiftId, Guid eventSettingsId, bool privileged, CancellationToken ct = default) =>
        WriteAsync(
            () => SignupsAsync(s => s.ToggleDayAsync(userId, shiftId, eventSettingsId, privileged, ct)),
            () => RotaOfShift(shiftId));

    public Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId) =>
        WriteAsync(() => SignupsAsync(s => s.ApproveAsync(signupId, reviewerUserId)), () => RotaOfSignup(signupId));

    public Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason) =>
        WriteAsync(() => SignupsAsync(s => s.RefuseAsync(signupId, reviewerUserId, reason)), () => RotaOfSignup(signupId));

    public Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason) =>
        WriteAsync(() => SignupsAsync(s => s.BailAsync(signupId, actorUserId, reason)), () => RotaOfSignup(signupId));

    public Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId) =>
        WriteAsync(() => SignupsAsync(s => s.MarkNoShowAsync(signupId, reviewerUserId)), () => RotaOfSignup(signupId));

    public Task<SignupResult> RemoveSignupAsync(Guid signupId, Guid removedByUserId, string? reason) =>
        WriteAsync(
            () => SignupsAsync(s => s.RemoveSignupAsync(signupId, removedByUserId, reason)),
            () => RotaOfSignup(signupId));

    public Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId) =>
        WriteAsync(() => SignupsAsync(s => s.ApproveRangeAsync(signupBlockId, reviewerUserId)), () => RotaOfBlock(signupBlockId));

    public Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason) =>
        WriteAsync(
            () => SignupsAsync(s => s.RefuseRangeAsync(signupBlockId, reviewerUserId, reason)),
            () => RotaOfBlock(signupBlockId));

    public Task<BailRangeResult> BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null) =>
        WriteAsync(
            () => SignupsAsync(s => s.BailRangeAsync(signupBlockId, actorUserId, reason)),
            () => RotaOfBlock(signupBlockId));

    // User-wide writes are rare, so they reload every rota rather than track which ones a user touches.
    public Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default) =>
        WriteAsync(() => SignupsAsync(s => s.CancelActiveSignupsForUserAsync(userId, reason, ct)), AllRotas);

    public Task<int> DeleteAllForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        WriteAsync(() => SignupsAsync(s => s.DeleteAllForUsersAsync(userIds, ct)), AllRotas);

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        WriteAsync(() => WithInnerAsync<ShiftSignupService>(s => s.EraseForUserAsync(userId, ct)), AllRotas);

    // Merge also rewrites reviewer and enroller ids, so every rota can change.
    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        WriteAsync(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            foreach (var inner in scope.ServiceProvider.GetKeyedServices<IUserMerge>(InnerServiceKey))
                await inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct);
        }, AllRotas);

    // ── Writes that don't change rotas, shifts or signups ───────────────

    public Task SaveKnobsAsync(
        Guid eventSettingsId, bool isShiftBrowsingOpen, BuildSubPeriodStarts subPeriods, Guid actorUserId) =>
        MgmtAsync(m => m.SaveKnobsAsync(eventSettingsId, isShiftBrowsingOpen, subPeriods, actorUserId));

    public Task SetShiftBrowsingOpenAsync(Guid eventSettingsId, bool isOpen) =>
        MgmtAsync(m => m.SetShiftBrowsingOpenAsync(eventSettingsId, isOpen));

    public Task UpdateShiftProfileAsync(ShiftVolunteerProfileInfo profile) =>
        MgmtAsync(m => m.UpdateShiftProfileAsync(profile));

    public Task<bool> SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds) =>
        MgmtAsync(m => m.SetVolunteerTagPreferencesAsync(userId, tagIds));

    public Task<ShiftTagSummary> GetOrCreateTagAsync(string name) =>
        MgmtAsync(m => m.GetOrCreateTagAsync(name));

    public void InvalidateDashboardCaches(Guid eventSettingsId)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredKeyedService<IShiftManagement>(InnerServiceKey)
            .InvalidateDashboardCaches(eventSettingsId);
    }

    // ── Reads, passed through until each moves onto the rows ────────────

    public Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default) =>
        MgmtAsync(m => m.IsDeptCoordinatorAsync(userId, departmentTeamId, ct));

    public Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default) =>
        MgmtAsync(m => m.CanApproveSignupsAsync(userId, departmentTeamId, ct));

    public Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetCoordinatorTeamIdsAsync(userId, ct));

    public Task<IReadOnlyList<RotaSearchHit>> SearchAsync(
        string query, int max, CancellationToken cancellationToken = default) =>
        MgmtAsync(m => m.SearchAsync(query, max, cancellationToken));

    public Task<RotaInfo?> GetRotaAsync(Guid rotaId, CancellationToken cancellationToken = default) =>
        MgmtAsync(m => m.GetRotaAsync(rotaId, cancellationToken));

    public Task<IReadOnlyList<UrgentShiftInfo>> GetBrowseShiftsAsync(ShiftBrowseQuery query, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetBrowseShiftsAsync(query, ct));

    public Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
        Guid eventSettingsId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetShiftsSummaryAsync(eventSettingsId, teamIds, ct));

    public Task<IReadOnlyDictionary<Guid, int>> GetActivePendingShiftSignupCountsByTeamAsync(
        CancellationToken cancellationToken = default) =>
        MgmtAsync(m => m.GetActivePendingShiftSignupCountsByTeamAsync(cancellationToken));

    public Task<bool> HasQualifyingCantinaSignupAsync(Guid userId, CancellationToken ct = default) =>
        MgmtAsync(m => m.HasQualifyingCantinaSignupAsync(userId, ct));

    public Task<IReadOnlyList<Guid>> GetOnSiteUserIdsForDayAsync(
        Guid eventSettingsId, int dayOffset, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetOnSiteUserIdsForDayAsync(eventSettingsId, dayOffset, ct));

    public Task<ShiftVolunteerProfileInfo?> GetShiftProfileAsync(Guid userId, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetShiftProfileAsync(userId, ct));

    public Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetTagsAsync(query, ct));

    public Task<IReadOnlyList<UrgentShiftInfo>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null, Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, bool includeRestricted = false, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetUrgentShiftsAsync(
            eventSettingsId, limit, departmentId, startDate, endDate, period, subPeriod, includeRestricted, ct));

    public Task<ShiftStaffingSnapshot> GetStaffingSnapshotAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetStaffingSnapshotAsync(eventSettingsId, departmentId, period, subPeriod, ct));

    public Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetDepartmentsWithRotasAsync(eventSettingsId, ct));

    public Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default) =>
        MgmtAsync(m => m.GetOverallCoverageAsync(ct));

    public Task<IReadOnlyList<ShiftAdminRota>> GetRotasByDepartmentAsync(
        Guid teamId, Guid eventSettingsId, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetRotasByDepartmentAsync(teamId, eventSettingsId, ct));

    public Task<ShiftEventKnobs?> GetKnobsAsync(Guid eventSettingsId, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetKnobsAsync(eventSettingsId, ct));

    public Task<ShiftSummary?> BuildSummaryAsync(
        BurnSettingsInfo activeEvent, string? teamSlug = null, Guid? rotaId = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.BuildSummaryAsync(activeEvent, teamSlug, rotaId, ct));

    public Task<ShiftWithRota?> GetShiftByIdAsync(Guid shiftId, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetShiftByIdAsync(shiftId, ct));

    public Task<IReadOnlyList<DepartmentCoveragePie>> GetDepartmentCoveragePiesAsync(
        Guid eventSettingsId, LocalDate? fromDate = null, LocalDate? toDate = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetDepartmentCoveragePiesAsync(eventSettingsId, fromDate, toDate, ct));

    public Task<DashboardOverview> GetDashboardOverviewAsync(
        Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetDashboardOverviewAsync(eventSettingsId, period, subPeriod, ct));

    public Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(
        Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetCoordinatorActivityAsync(eventSettingsId, period, subPeriod, ct));

    public Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetDashboardTrendsAsync(eventSettingsId, window, period, subPeriod, ct));

    public Task<IReadOnlyList<DailyDepartmentStaffing>> GetDailyDepartmentStaffingAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetDailyDepartmentStaffingAsync(eventSettingsId, period, subPeriod, ct));

    public Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetShiftDurationBreakdownAsync(eventSettingsId, period, subPeriod, ct));

    public Task<CoverageHeatmap> GetCoverageHeatmapAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetCoverageHeatmapAsync(eventSettingsId, period, subPeriod, ct));

    public Task<PostEventStats?> GetPostEventStatsAsync(Guid eventSettingsId, CancellationToken ct = default) =>
        MgmtAsync(m => m.GetPostEventStatsAsync(eventSettingsId, ct));

    public Task<IReadOnlyList<NoShowHistoryEntry>> GetNoShowHistoryAsync(Guid userId, CancellationToken ct = default) =>
        SignupsAsync(s => s.GetNoShowHistoryAsync(userId, ct));

    public Task<IReadOnlyList<ShiftSignupSummary>> GetByUserAsync(
        Guid userId, Guid? eventSettingsId = null, CancellationToken ct = default) =>
        SignupsAsync(s => s.GetByUserAsync(userId, eventSettingsId, ct));

    public Task<ShiftSignupTeamProbe?> GetTeamProbeAsync(
        Guid id, ShiftSignupTeamProbeScope scope, CancellationToken ct = default) =>
        SignupsAsync(s => s.GetTeamProbeAsync(id, scope, ct));

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => ShiftSignupService.Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync<ShiftSignupService, IReadOnlyList<UserDataSlice>>(s => s.ContributeForUserAsync(userId, ct));

    // ── Inner access ────────────────────────────────────────────────────

    private Task<T> MgmtAsync<T>(Func<IShiftManagement, Task<T>> call) => WithInnerAsync<IShiftManagement, T>(call);

    private Task MgmtAsync(Func<IShiftManagement, Task> call) => WithInnerAsync<IShiftManagement>(call);

    private Task<T> SignupsAsync<T>(Func<IShiftSignupService, Task<T>> call) => WithInnerAsync<IShiftSignupService, T>(call);

    private Task<T> RowsAsync<T>(Func<IShiftRotaRows, Task<T>> call) => WithInnerAsync<IShiftRotaRows, T>(call);

    private async Task WithInnerAsync<TInner>(Func<TInner, Task> call) where TInner : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await call(scope.ServiceProvider.GetRequiredKeyedService<TInner>(InnerServiceKey));
    }

    private async Task<T> WithInnerAsync<TInner, T>(Func<TInner, Task<T>> call) where TInner : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredKeyedService<TInner>(InnerServiceKey));
    }
}
