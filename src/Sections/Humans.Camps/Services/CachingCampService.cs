using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.Camps.Services;

/// <summary>
/// Year-keyed reads are filtered snapshots, not separate cache entries. Also carries <see cref="ICampRoleService"/>
/// and <see cref="IUserDataContributor"/>: role writes and GDPR erasure change rows the projection serves.
/// </summary>
internal sealed class CachingCampService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<CachingCampService> logger) : TrackedCache<Guid, CampInfo>("Camp.CampInfo", warmOnStartup: true, logger),
    ICampManagement, ICampLeadDirectory, ICampSeeding, ICampRoleCampAccess, IUserMerge, ICampRoleService, IUserDataContributor, IEarlyEntryProvider
{
    /// <summary>DI key for the undecorated inner <see cref="ICampManagement"/>.</summary>
    public const string InnerServiceKey = "camp-inner";

    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private CampSettingsInfo? _settings;
    // Years populated by warmup; cold-year reads fall back to the inner service.
    private volatile IReadOnlySet<int>? _warmYears;

    public async Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);
        var normalizedSlug = slug.ToLowerInvariant();
        var cached = Values.FirstOrDefault(camp =>
            string.Equals(camp.Slug, normalizedSlug, StringComparison.Ordinal));
        return cached ?? await WithInnerAsync(inner => inner.GetCampBySlugAsync(slug, cancellationToken));
    }

    public async Task<CampInfo?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        // Cold camps (a year warmup never covered) fall back to the inner service.
        await EnsureWarmedAsync(cancellationToken);
        return TryGet(campId, out var cached)
            ? cached
            : await WithInnerAsync(inner => inner.GetCampByIdAsync(campId, cancellationToken));
    }

    public async Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);
        // Cold year — fall back to inner service so admin/year-driven flows
        // that pass arbitrary years still get correct results.
        if (!IsWarmYear(year))
        {
            return await WithInnerAsync(inner => inner.GetCampsForYearAsync(year, cancellationToken));
        }

        var snapshot = Values;
        var result = new List<CampInfo>(snapshot.Count);
        foreach (var camp in snapshot)
        {
            if (camp.Seasons.Any(s => s.Year == year))
            {
                result.Add(FilterToYear(camp, year));
            }
        }
        return result;
    }

    private bool IsWarmYear(int year)
    {
        var snapshot = _warmYears;
        return snapshot is not null && snapshot.Contains(year);
    }

    public async Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _settings ?? await LoadSettingsAsync(cancellationToken);
        // PublicYear is Settings-owned: activating a new event changes it, and nothing in
        // Settings signals this section's invalidator. Only the camps-owned parts of the
        // snapshot are cached; the year is resolved live on every read.
        return snapshot with { PublicYear = await GetActiveYearAsync(cancellationToken) };
    }

    public Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct) =>
        WithInnerAsync(inner => inner.GetEarlyEntriesAsync(ct));

    public Task<int> GetActiveYearAsync(CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetActiveYearAsync(cancellationToken));

    public async Task<CampUserInfo> GetCampUserInfoAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // PublicYear is always a warm year (warmup seeds it), so both calls below
        // are served from the dict — no DB round-trip for the human card / Shifts view.
        var settings = await GetSettingsAsync(cancellationToken);
        var camps = await GetCampsForYearAsync(settings.PublicYear, cancellationToken);
        return CampUserInfo.Resolve(camps, settings.PublicYear, userId);
    }

    public Task<CampEditData?> GetCampEditDataAsync(
        Guid campId, int? preferredYear = null, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetCampEditDataAsync(campId, preferredYear, cancellationToken));

    // A camp surfaces in global search only when its public-year season is publicly visible.
    private static readonly CampSeasonStatus[] PublicCampSeasonStatuses =
        [CampSeasonStatus.Active, CampSeasonStatus.Full];

    public async Task<IReadOnlyList<CampSearchHit>> SearchAsync(
        string query, int max, CancellationToken cancellationToken = default)
    {
        // Served from the cached CampInfo snapshot — search must never hit the DB. PublicYear is
        // always a warm year, so both the settings read and GetCampsForYearAsync stay in-cache.
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return [];

        var settings = await GetSettingsAsync(cancellationToken);
        var year = settings.PublicYear;

        // A pasted GUID jumps straight to that camp (any status).
        if (Guid.TryParse(query, out var id))
        {
            await EnsureWarmedAsync(cancellationToken);
            var camp = Values.FirstOrDefault(c => c.Id == id);
            if (camp is null)
                return [];
            var byIdSeason = camp.Seasons.FirstOrDefault(s => s.Year == year);
            return
            [
                new CampSearchHit(
                    camp.Id, byIdSeason?.Name ?? camp.Slug, StringSearchExtensions.ExactNameScore)
            ];
        }

        // Case-insensitive name match against the public-year season name, public statuses only.
        var camps = await GetCampsForYearAsync(year, cancellationToken);
        var trimmed = query.Trim();
        var hits = new List<CampSearchHit>();
        foreach (var camp in camps)
        {
            var season = camp.Seasons.FirstOrDefault(s =>
                s.Year == year
                && PublicCampSeasonStatuses.Contains(s.Status)
                && s.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
            if (season is not null)
                hits.Add(new CampSearchHit(camp.Id, season.Name, season.Name.NameMatchScore(trimmed)));
        }

        return hits.Take(max).ToList();
    }

    public async Task<CampSeasonInfo?> GetCampSeasonByIdAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);
        foreach (var camp in Values)
        {
            var season = camp.Seasons.FirstOrDefault(s => s.Id == campSeasonId);
            if (season is not null) return season;
        }

        return await WithInnerAsync(inner => inner.GetCampSeasonByIdAsync(campSeasonId, cancellationToken));
    }

    public Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetCampMemberStatusAsync(campMemberId, cancellationToken));

    public async Task<IReadOnlyList<(Guid CampId, string CampName, string CampSlug, Guid CampSeasonId,
            CampSeasonStatus Status, int TargetMemberCount, int? JoinedMemberCount)>>
        GetCampSeasonsForComplianceAsync(int year, CancellationToken ct = default)
    {
        var camps = await GetCampsForYearAsync(year, ct);
        return camps
            .SelectMany(camp => camp.Seasons
                .Where(season => season.Year == year)
                .Select(season => (
                    CampId: camp.Id,
                    CampName: season.Name,
                    CampSlug: camp.Slug,
                    CampSeasonId: season.Id,
                    season.Status,
                    TargetMemberCount: season.MemberCount,
                    season.JoinedMemberCount)))
            .ToList();
    }

    public Task<CampWriteResult<Camp>> CreateCampAsync(
        Guid createdByUserId, string name, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        CampSeasonData seasonData, List<string>? historicalNames, int year,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var camp = await WithInnerAsync(inner => inner.CreateCampAsync(
                createdByUserId, name, contactEmail, contactPhone, webOrSocialUrl,
                links, isSwissCamp, timesAtNowhere, seasonData, historicalNames, year,
                cancellationToken));
            if (camp.Value is { } created) await InvalidateCampAsync(created.Id, cancellationToken);
            return camp;
        });

    public Task<CampWriteResult<CampSeason>> OptInToSeasonAsync(
        Guid campId, int year, Guid? actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.OptInToSeasonAsync(campId, year, actorUserId: actorUserId, cancellationToken));
            await InvalidateCampAsync(campId, cancellationToken);
            return result;
        });

    public Task<CampUpdateResult> UpdateSeasonAsync(
        Guid scopedCampId, Guid seasonId, CampSeasonData data, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.UpdateSeasonAsync(scopedCampId, seasonId, data, actorUserId: actorUserId, cancellationToken));
            await InvalidateBySeasonAsync(seasonId, cancellationToken);
            return result;
        });

    public Task<CampAdminMutationResult> ApproveSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.ApproveSeasonAsync(seasonId, reviewedByUserId, notes, cancellationToken));
            if (result.Succeeded) await InvalidateBySeasonAsync(seasonId, cancellationToken);
            return result;
        });

    public Task<CampAdminMutationResult> RejectSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.RejectSeasonAsync(seasonId, reviewedByUserId, notes, cancellationToken));
            if (result.Succeeded) await InvalidateBySeasonAsync(seasonId, cancellationToken);
            return result;
        });

    public Task<CampUpdateResult> WithdrawSeasonAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.WithdrawSeasonAsync(scopedCampId, seasonId, actorUserId: actorUserId, cancellationToken));
            await InvalidateBySeasonAsync(seasonId, cancellationToken);
            return result;
        });

    public Task<CampAdminMutationResult> ReactivateSeasonAsync(Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.ReactivateSeasonAsync(seasonId, actorUserId: actorUserId, cancellationToken));
            if (result.Succeeded) await InvalidateBySeasonAsync(seasonId, cancellationToken);
            return result;
        });

    public Task<CampUpdateResult> MarkSeasonFullAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.MarkSeasonFullAsync(scopedCampId, seasonId, actorUserId: actorUserId, cancellationToken));
            await InvalidateBySeasonAsync(seasonId, cancellationToken);
            return result;
        });

    public Task<CampUpdateResult> UpdateCampAsync(
        CampUpdateInput input, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.UpdateCampAsync(input, actorUserId: actorUserId, cancellationToken));
            // The inner can report failure after committing camp fields.
            await InvalidateCampAsync(input.CampId, cancellationToken);
            return result;
        });

    public Task<CampAdminMutationResult> DeleteCampAsync(Guid campId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.DeleteCampAsync(campId, actorUserId: actorUserId, cancellationToken));
            // Tombstone without flipping warmth — preserves the all-rows invariant.
            DeleteKey(campId);
            return result;
        });

    public Task<CampUpdateResult> AddHistoricalNameAsync(
        Guid campId, string name, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.AddHistoricalNameAsync(campId, name, cancellationToken));
            await InvalidateCampAsync(campId, cancellationToken);
            return result;
        });

    public Task<CampUpdateResult> RemoveHistoricalNameAsync(
        Guid scopedCampId, Guid historicalNameId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.RemoveHistoricalNameAsync(scopedCampId, historicalNameId, cancellationToken));
            await InvalidateCampAsync(scopedCampId, cancellationToken);
            return result;
        });

    public Task<CampImageUploadResult> UploadImageAsync(
        Guid campId, Stream fileStream, string fileName, string contentType, long length,
        Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.UploadImageAsync(
                campId, fileStream, fileName, contentType, length, actorUserId: actorUserId, cancellationToken));
            if (result.Succeeded)
                await InvalidateCampAsync(campId, cancellationToken);
            return result;
        });

    public Task<CampUpdateResult> DeleteImageAsync(
        Guid scopedCampId, Guid imageId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.DeleteImageAsync(scopedCampId, imageId, actorUserId: actorUserId, cancellationToken));
            await InvalidateCampAsync(scopedCampId, cancellationToken);
            return result;
        });

    public Task ReorderImagesAsync(
        Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.ReorderImagesAsync(campId, imageIdsInOrder, cancellationToken));
            await InvalidateCampAsync(campId, cancellationToken);
        });

    public Task OpenSeasonAsync(int year, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.OpenSeasonAsync(year, actorUserId, cancellationToken));
            await InvalidateSettingsAsync(cancellationToken);
        });

    public Task CloseSeasonAsync(int year, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.CloseSeasonAsync(year, actorUserId, cancellationToken));
            await InvalidateSettingsAsync(cancellationToken);
        });

    public Task SetNameLockDateAsync(
        int year, LocalDate lockDate, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.SetNameLockDateAsync(year, lockDate, actorUserId, cancellationToken));
            // Touches every season in the year — RefreshAll.
            RefreshAll();
            await InvalidateSettingsAsync(cancellationToken);
        });

    public Task<CampUpdateResult> ChangeSeasonNameAsync(
        Guid scopedCampId, Guid seasonId, string newName, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.ChangeSeasonNameAsync(scopedCampId, seasonId, newName, actorUserId: actorUserId, cancellationToken));
            await InvalidateBySeasonAsync(seasonId, cancellationToken);
            return result;
        });

    // Membership writes — invalidate the parent camp (EeGrantedCount + MemberCount move).

    public Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.RequestCampMembershipAsync(campId, userId, cancellationToken));
            await InvalidateCampAsync(campId, cancellationToken);
            return result;
        });

    public Task<CampMembershipMutationResult> ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.ApproveCampMemberAsync(scopedCampId, campMemberId, approvedByUserId, cancellationToken));
            await InvalidateCampAsync(scopedCampId, cancellationToken);
            return result;
        });

    public Task<CampMembershipMutationResult> RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.RejectCampMemberAsync(scopedCampId, campMemberId, rejectedByUserId, cancellationToken));
            await InvalidateCampAsync(scopedCampId, cancellationToken);
            return result;
        });

    public Task<CampMembershipMutationResult> RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.RemoveCampMemberAsync(scopedCampId, campMemberId, removedByUserId, cancellationToken));
            await InvalidateCampAsync(scopedCampId, cancellationToken);
            return result;
        });

    public Task<AddCampMemberOutcome> AddCampMemberToActiveSeasonAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, int? year = null) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.AddCampMemberToActiveSeasonAsync(
                campId, userId, actorUserId, cancellationToken, year));
            await InvalidateCampAsync(campId, cancellationToken);
            return result;
        });

    public Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, int? year = null) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.AddMemberAndAssignRoleInActiveSeasonAsync(
                campId, roleDefinitionId, userId, actorUserId, cancellationToken, year));
            await InvalidateCampAsync(campId, cancellationToken);
            return result;
        });

    public Task<CampMembershipMutationResult> WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            // No campId on the API surface — RefreshAll.
            var result = await WithInnerAsync(inner => inner.WithdrawCampMembershipRequestAsync(campMemberId, userId, cancellationToken));
            RefreshAll();
            return result;
        });

    public Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            // No campId on the API surface; can move EeGrantedCount — RefreshAll on success.
            var result = await WithInnerAsync(inner => inner.LeaveCampAsync(campMemberId, userId, cancellationToken));
            if (result.Succeeded)
                RefreshAll();
            return result;
        });

    public Task<CampAdminMutationResult> SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.SetCampSeasonEeSlotCountAsync(campSeasonId, slotCount, actorUserId, cancellationToken));
            if (result.Succeeded)
                await InvalidateBySeasonAsync(campSeasonId, cancellationToken);
            return result;
        });

    public Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
        Guid scopedCampId, Guid campMemberId, bool granted, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerAsync(inner => inner.SetEarlyEntryAsync(
                scopedCampId, campMemberId, granted, actorUserId, cancellationToken));
            await InvalidateCampAsync(scopedCampId, cancellationToken);
            return result;
        });

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct) =>
        MutateAsync(async () =>
        {
            await WithInnerMergeAsync(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));
            // Lead reassignment can touch any camp — RefreshAll.
            RefreshAll();
        });

    // ICampRoleService — role-definition edits and assignment changes reshape the cached
    // role projection, so each write that changed something drops it. Reads pass through.

    Task<IReadOnlyList<CampRoleDefinitionInfo>> ICampRoleService.ListDefinitionsAsync(
        bool includeDeactivated, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.ListDefinitionsAsync(includeDeactivated, ct));

    Task<CampRoleDefinitionInfo?> ICampRoleService.GetDefinitionByIdAsync(Guid id, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.GetDefinitionByIdAsync(id, ct));

    Task<CampRoleDefinitionInfo?> ICampRoleService.GetDefinitionBySlugAsync(string slug, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.GetDefinitionBySlugAsync(slug, ct));

    string ICampRoleService.BuildGroupKey(int year, string slug)
    {
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredKeyedService<ICampRoleService>(InnerServiceKey)
            .BuildGroupKey(year, slug);
    }

    Task<CampRoleDrillDownData?> ICampRoleService.BuildDrillDownAsync(
        Guid roleDefinitionId, int year, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.BuildDrillDownAsync(roleDefinitionId, year, ct));

    // A new definition has no assignments yet, so nothing cached changes.
    Task<CampAdminMutationResult> ICampRoleService.CreateDefinitionAsync(
        CampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.CreateDefinitionAsync(input, actorUserId, ct));

    // Name/SortOrder are baked into the role projection of every camp that assigns the
    // definition, and (de)activation drops or restores it there: no single camp to target.
    Task<UpdateCampRoleDefinitionResult> ICampRoleService.UpdateDefinitionAsync(
        Guid id, CampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct) =>
        MutateRolesAsync(
            inner => inner.UpdateDefinitionAsync(id, input, actorUserId, ct),
            result => result.Status == UpdateCampRoleDefinitionStatus.Updated);

    Task<bool> ICampRoleService.DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct) =>
        MutateRolesAsync(inner => inner.DeactivateDefinitionAsync(id, actorUserId, ct), changed => changed);

    Task<bool> ICampRoleService.ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct) =>
        MutateRolesAsync(inner => inner.ReactivateDefinitionAsync(id, actorUserId, ct), changed => changed);

    Task<CampRolesPanelData> ICampRoleService.BuildPanelAsync(Guid campSeasonId, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.BuildPanelAsync(campSeasonId, ct));

    Task<AssignCampRoleOutcome> ICampRoleService.AssignAsync(
        Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct) =>
        MutateRolesAsync(
            inner => inner.AssignAsync(campSeasonId, roleDefinitionId, campMemberId, actorUserId, ct),
            outcome => outcome == AssignCampRoleOutcome.Assigned);

    Task<CampRoleAssignmentInfo?> ICampRoleService.GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.GetAssignmentByIdAsync(assignmentId, ct));

    Task<bool> ICampRoleService.UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct) =>
        MutateRolesAsync(inner => inner.UnassignAsync(assignmentId, actorUserId, ct), deleted => deleted);

    // Also the Leave/Withdraw/Remove cascade: CampService reaches it through this decorator.
    Task<int> ICampRoleService.RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct) =>
        MutateRolesAsync(inner => inner.RemoveAllForMemberAsync(campMemberId, actorUserId, ct), deleted => deleted > 0);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<CampDirectoryRoleSummary>>>
        ICampRoleService.GetDirectoryRoleSummariesAsync(int year, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.GetDirectoryRoleSummariesAsync(year, ct));

    Task<CampComplianceMatrixData> ICampRoleService.BuildComplianceMatrixAsync(int year, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.BuildComplianceMatrixAsync(year, ct));

    // Seeding creates definitions only; no assignment, so nothing cached changes.
    Task<int> ICampRoleService.SeedSystemRolesAsync(Guid actorUserId, CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.SeedSystemRolesAsync(actorUserId, ct));

    Task<IReadOnlyList<CampSpecialRole>> ICampRoleService.GetMissingSpecialRolesAsync(CancellationToken ct) =>
        WithInnerRolesAsync(inner => inner.GetMissingSpecialRolesAsync(ct));

    // IUserDataContributor — erasure empties the rosters the projection carries. The user's
    // camps span an unbounded set and the delete does not hand back their ids: RefreshAll.

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => CampService.Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync(inner => inner.ContributeForUserAsync(userId, ct));

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(async () =>
        {
            await WithInnerAsync(inner => inner.EraseForUserAsync(userId, ct));
            RefreshAll();
        });

    // Per-camp precision is aspirational: every invalidation drops the whole projection. campId feeds the debug log
    // and keeps call sites naming the changed camp, so a narrower rebuild can land here without touching them.
    private Task InvalidateCampAsync(Guid campId, CancellationToken ct)
    {
        logger.LogDebug("CampInfo invalidate campId={CampId}", campId);
        RefreshAll();
        return Task.CompletedTask;
    }

    private async Task InvalidateSettingsAsync(CancellationToken ct)
    {
        await _settingsLock.WaitAsync(ct);
        try
        {
            _settings = null;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>
    /// Discovers camps in PublicYear ∪ OpenSeasons ∪ currentYear, then caches their full history.
    /// </summary>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var years = new HashSet<int>();
        years.Add(settings.PublicYear);
        foreach (var y in settings.OpenSeasons) years.Add(y);
        years.Add(SystemClockYear());

        var campIds = new HashSet<Guid>();
        foreach (var year in years)
        {
            var camps = await WithInnerAsync(inner => inner.GetCampsForYearAsync(year, ct));
            foreach (var camp in camps)
            {
                campIds.Add(camp.Id);
            }
        }

        foreach (var campId in campIds)
        {
            // Slug/id reads also authorize leads from earlier seasons. A year-filtered
            // projection cannot serve as the canonical camp, even on a warm cache hit.
            var camp = await WithInnerAsync(inner => inner.GetCampByIdAsync(campId, ct));
            if (camp is not null) Set(campId, camp);
        }

        _warmYears = years;
    }

    private int SystemClockYear() => clock.GetCurrentInstant().InUtc().Year;

    /// <summary>Drop the dict; next read triggers a fresh <see cref="WarmAllAsync"/>.</summary>
    private void RefreshAll()
    {
        // Drop _warmYears too so cold-year requests fall back, not read empty dict.
        _warmYears = null;
        Clear();
    }

    private async Task<CampSettingsInfo> LoadSettingsAsync(CancellationToken ct)
    {
        await _settingsLock.WaitAsync(ct);
        try
        {
            if (_settings is not null) return _settings;
            _settings = await WithInnerAsync(inner => inner.GetSettingsAsync(ct));
            return _settings;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task InvalidateBySeasonAsync(Guid seasonId, CancellationToken ct)
    {
        foreach (var camp in Values)
        {
            if (camp.Seasons.Any(s => s.Id == seasonId))
            {
                await InvalidateCampAsync(camp.Id, ct);
                return;
            }
        }
        var season = await WithInnerAsync(inner => inner.GetCampSeasonByIdAsync(seasonId, ct));
        if (season is not null)
            await InvalidateCampAsync(season.CampId, ct);
    }

    private static CampInfo FilterToYear(CampInfo camp, int year) =>
        camp with
        {
            Seasons = camp.Seasons.Where(s => s.Year == year).ToList()
        };

    // Writes and their invalidation can fail after committing. Clear both cache shapes
    // before propagating the original failure; cleanup must survive request cancellation.
    private async Task<T> MutateAsync<T>(Func<Task<T>> mutation)
    {
        try
        {
            return await mutation();
        }
        catch
        {
            RefreshAll();
            await InvalidateSettingsAsync(CancellationToken.None);
            throw;
        }
    }

    private Task MutateAsync(Func<Task> mutation) =>
        MutateAsync(async () =>
        {
            await mutation();
            return true;
        });

    private async Task<T> WithInnerAsync<T>(Func<ICampManagement, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampManagement>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInnerAsync(Func<ICampManagement, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampManagement>(InnerServiceKey);
        await work(inner);
    }

    private async Task WithInnerMergeAsync(Func<IUserMerge, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserMerge>(InnerServiceKey);
        await work(inner);
    }

    private async Task<T> WithInnerRolesAsync<T>(Func<ICampRoleService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampRoleService>(InnerServiceKey);
        return await work(inner);
    }

    // Drops the projection after a role write that changed something.
    private Task<T> MutateRolesAsync<T>(Func<ICampRoleService, Task<T>> write, Func<T, bool> changed) =>
        MutateAsync(async () =>
        {
            var result = await WithInnerRolesAsync(write);
            if (changed(result)) RefreshAll();
            return result;
        });
    // Leaf carves: pass-throughs, since a decorator may not reach the repository itself.

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken cancellationToken = default) =>
        WithInnerLeadDirectoryAsync(inner => inner.GetActiveLeadUserIdsAsync(cancellationToken));

    /// <inheritdoc />
    public Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken cancellationToken = default) =>
        WithInnerLeadDirectoryAsync(inner => inner.IsLeadAnywhereAsync(userId, cancellationToken));

    /// <inheritdoc />
    /// <remarks>
    /// Goes through this decorator's own <see cref="CreateCampAsync"/> rather than the inner
    /// service, so the new camp lands in the snapshot before the call returns. Routing it
    /// straight to the inner service would leave every list-based read —
    /// <see cref="GetCampsForYearAsync"/>, <see cref="SearchAsync"/>,
    /// <see cref="GetCampUserInfoAsync"/> — blind to the seeded camp until some later mutation
    /// happened to invalidate it.
    /// </remarks>
    async Task<Guid> ICampSeeding.CreateCampForSeedAsync(
        Guid createdByUserId, string name, string contactEmail, string contactPhone,
        bool isSwissCamp, int timesAtNowhere, CampSeasonData seasonData, int year,
        CancellationToken cancellationToken)
    {
        var camp = await CreateCampAsync(
            createdByUserId, name, contactEmail, contactPhone,
            webOrSocialUrl: null, links: [], isSwissCamp, timesAtNowhere,
            seasonData, historicalNames: [], year, cancellationToken);
        return camp.Value?.Id ?? throw new InvalidOperationException("Camp seed registration was refused.");
    }

    /// <inheritdoc />
    async Task ICampSeeding.OptInToSeasonAsync(Guid campId, int year, CancellationToken cancellationToken)
    {
        var result = await OptInToSeasonAsync(campId, year, actorUserId: null, cancellationToken);
        if (result.ErrorKey is not null) throw new InvalidOperationException("Camp seed season opt-in was refused.");
    }

    /// <inheritdoc />
    async Task ICampSeeding.ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken)
    {
        var result = await ApproveSeasonAsync(seasonId, reviewedByUserId, notes, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(result.ErrorMessage);
    }

    /// <inheritdoc />
    async Task ICampSeeding.AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken) =>
        await AddMemberAndAssignRoleInActiveSeasonAsync(
            campId, roleDefinitionId, userId, actorUserId, cancellationToken);

    private async Task<T> WithInnerLeadDirectoryAsync<T>(Func<ICampLeadDirectory, Task<T>> work)
    {
        using var scope = scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampManagement>(InnerServiceKey);
        return await work((ICampLeadDirectory)inner);
    }


}
