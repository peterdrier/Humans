using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Infrastructure.Services.Camps;

/// <summary>
/// T-06 (issue: 2026-05-16 cache-migration plan). Singleton transparent
/// caching decorator for <see cref="ICampService"/>. Inherits
/// <see cref="TrackedCache{TKey,TValue}"/> for a hit/miss/invalidation-tracked
/// dict of <see cref="CampInfo"/> entries keyed by camp id — the canonical
/// per-camp read-model. Settings (<see cref="CampSettingsInfo"/>) is held as
/// a separate single-slot cache.
/// </summary>
/// <remarks>
/// <para>
/// Year-keyed sub-views (<see cref="GetCampsForYearAsync"/>,
/// <see cref="GetCampsWithLeadsForYearAsync"/>, summary projections) are
/// filtered SNAPSHOTS of the per-camp cache — not separate cache entries.
/// This collapses the legacy <c>camps_year_{year}</c> + <c>CampSettings</c>
/// short-TTL <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
/// keys into one §15-shaped projection.
/// </para>
/// <para>
/// Invalidation is decorator-mediated only. Every mutating method in this
/// class delegates to the inner service then calls
/// <see cref="ICampInfoInvalidator.InvalidateCampAsync"/> /
/// <see cref="ICampInfoInvalidator.InvalidateSettingsAsync"/>. There is no
/// SaveChanges interceptor backstop — the no-bypass rule is pinned by
/// <c>CampsArchitectureTests</c>: only the inner <c>CampService</c> and
/// <c>CampRoleService</c> may touch <c>ICampRepository</c>, so every write
/// to the Camps tables (including the cross-table
/// <c>camp_members.HasEarlyEntry</c> dependency that
/// <see cref="CampSeasonInfo.EeGrantedCount"/> projects from) is routed
/// through an <see cref="ICampService"/> method this decorator wraps.
/// </para>
/// <para>
/// Cache size budget: ~5 MB at ~100 camps / 500-user scale, well under the
/// §15 50-MB ceiling. See <see cref="CampInfo"/> for the per-entry breakdown.
/// </para>
/// </remarks>
public sealed class CachingCampService :
    TrackedCache<Guid, CampInfo>,
    ICampService,
    IUserMerge,
    ICampInfoInvalidator
{
    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="ICampService"/>
    /// is registered. Used by the singleton decorator to resolve the scoped inner
    /// service per call.
    /// </summary>
    public const string InnerServiceKey = "camp-inner";

    private readonly ICampRepository _repo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly ILogger<CachingCampService> _logger;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private CampSettingsInfo? _settings;
    // Set of years the warmup populated into the dict. Years outside this set
    // are NOT served from the snapshot — GetCampsForYearAsync falls back to
    // the inner service so admin/year-driven flows that pass arbitrary years
    // (CityPlanning, EventsApi, ContainerService) still see correct results.
    // Replaced wholesale by WarmAllAsync; reads are tear-aware via the
    // volatile reference swap.
    private volatile IReadOnlySet<int>? _warmYears;

    public CachingCampService(
        ICampRepository repo,
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingCampService> logger)
        : base("Camp.CampInfo", warmOnStartup: true, logger)
    {
        _repo = repo;
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // Cached reads
    // ==========================================================================

    public async Task<CampLookup?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        // Pass through to repo for the canonical entity-shaped read used by
        // controllers (CampLookup carries different fields than CampInfo).
        // CampInfo cache still warms on first access in WarmIfNeededAsync below.
        return await WithInner(inner => inner.GetCampBySlugAsync(slug, cancellationToken));
    }

    public async Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);
        // Warm scope is bounded to PublicYear ∪ OpenSeasons ∪ currentYear
        // (see WarmAllAsync). Requests for years outside that set must not
        // return an empty list — admin/year-driven flows (City Planning,
        // Events lookups, CSV exports) pass arbitrary years. For cold years
        // we fall back to the inner service so the result matches the
        // un-cached behavior. The cache snapshot stays bounded; cold-year
        // misses are rare and a single repo query is cheap.
        if (!IsWarmYear(year))
        {
            return await WithInner(inner => inner.GetCampsForYearAsync(year, cancellationToken));
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

#pragma warning disable CS0618
    public async Task<IReadOnlyList<CampInfo>> GetCampsWithLeadsForYearAsync(
        int year, IReadOnlyList<CampSeasonStatus>? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        // Deprecated alias: filter in-memory by season status. Behavior matches
        // the legacy repo-side filter (any season for the year in the allowed
        // status set means the camp is included).
        await EnsureWarmedAsync(cancellationToken);
        // Fall back to the inner service for cold years — same reasoning as
        // GetCampsForYearAsync. Cold-year + statusFilter is rare; one query.
        if (!IsWarmYear(year))
        {
            return await WithInner(inner => inner.GetCampsWithLeadsForYearAsync(year, statusFilter, cancellationToken));
        }
        var snapshot = Values;
        var result = new List<CampInfo>(snapshot.Count);
        foreach (var camp in snapshot)
        {
            var seasonsThisYear = camp.Seasons.Where(s => s.Year == year).ToList();
            if (seasonsThisYear.Count == 0) continue;
            if (statusFilter is { Count: > 0 } && !seasonsThisYear.Any(s => statusFilter.Contains(s.Status)))
                continue;
            result.Add(FilterToYear(camp, year));
        }
        return result;
    }
#pragma warning restore CS0618

    public async Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _settings;
        if (snapshot is not null) return snapshot;
        return await LoadSettingsAsync(cancellationToken);
    }

    public async Task<bool> IsUserCampLeadAsync(
        Guid userId, Guid campId, CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);
        if (TryGet(campId, out var camp))
        {
            return camp.Leads.Any(l => l.UserId == userId);
        }
        // Camp absent from cache — could be lazily-warmed race; fall through
        // to the inner service to avoid a false negative on auth checks.
        return await WithInner(inner => inner.IsUserCampLeadAsync(userId, campId, cancellationToken));
    }

    public async Task<Guid?> GetCampLeadSeasonIdForYearAsync(
        Guid userId, int year, CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);
        // Cold-year fallback — the snapshot only carries seasons for years in
        // the warm scope (PublicYear ∪ OpenSeasons ∪ currentYear), so a query
        // for a year outside that set would falsely return null. Same pattern
        // as GetCampsForYearAsync / GetCampsWithLeadsForYearAsync.
        if (!IsWarmYear(year))
        {
            return await WithInner(inner => inner.GetCampLeadSeasonIdForYearAsync(userId, year, cancellationToken));
        }
        foreach (var camp in Values)
        {
            if (!camp.Leads.Any(l => l.UserId == userId)) continue;
            var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
            if (season is not null) return season.Id;
        }
        return null;
    }

    // ==========================================================================
    // Pure pass-through reads (richer shapes than CampInfo / per-record paths)
    // ==========================================================================

    public Task<CampDetailData?> BuildCampDetailDataBySlugAsync(
        string slug, int? preferredYear = null, bool fallbackToLatestSeason = true,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.BuildCampDetailDataBySlugAsync(slug, preferredYear, fallbackToLatestSeason, cancellationToken));

    public Task<CampEditData?> GetCampEditDataAsync(
        Guid campId, int? preferredYear = null, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampEditDataAsync(campId, preferredYear, cancellationToken));

    public Task<CampDirectoryResult> GetCampDirectoryAsync(
        Guid? userId, CampDirectoryFilter? filter = null, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampDirectoryAsync(userId, filter, cancellationToken));

    public Task<IReadOnlyList<CampPublicSummary>> GetCampPublicSummariesForYearAsync(
        int year, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampPublicSummariesForYearAsync(year, cancellationToken));

    public Task<IReadOnlyList<CampPlacementSummary>> GetCampPlacementSummariesForYearAsync(
        int year, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampPlacementSummariesForYearAsync(year, cancellationToken));

    public Task<IReadOnlyList<CampSeasonInfo>> GetPendingSeasonsAsync(CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetPendingSeasonsAsync(cancellationToken));

    public Task<IReadOnlyList<CampSearchHit>> SearchAsync(
        string query, int max, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.SearchAsync(query, max, cancellationToken));

    public Task<CampSeasonLookup?> GetCampSeasonByIdAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampSeasonByIdAsync(campSeasonId, cancellationToken));

    public Task<IReadOnlyDictionary<Guid, CampSeasonDisplayData>> GetCampSeasonDisplayDataForYearAsync(
        int year, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken));

    public Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampMemberStatusAsync(campMemberId, cancellationToken));

    public Task<CampMembershipState> GetMembershipStateForCampAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetMembershipStateForCampAsync(campId, userId, cancellationToken));

    public Task<CampMemberListData> GetCampMembersAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampMembersAsync(campSeasonId, cancellationToken));

    public Task<IReadOnlyList<CampSeasonMemberInfo>> GetSeasonMembersAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetSeasonMembersAsync(campSeasonId, cancellationToken));

    public Task<IReadOnlyList<CampMembershipSummary>> GetCampMembershipsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampMembershipsForUserAsync(userId, cancellationToken));

    public Task<int> GetPendingMembershipCountForLeadAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetPendingMembershipCountForLeadAsync(userId, cancellationToken));

    public Task<IReadOnlyList<(Guid CampId, string CampName, string CampSlug, Guid CampSeasonId)>>
        GetCampSeasonsForComplianceAsync(int year, CancellationToken ct = default) =>
        WithInner(inner => inner.GetCampSeasonsForComplianceAsync(year, ct));

    public Task<Dictionary<int, LocalDate?>> GetNameLockDatesAsync(
        List<int> years, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetNameLockDatesAsync(years, cancellationToken));

    // ==========================================================================
    // Writes — delegate to inner, then invalidate the affected camp / settings
    // ==========================================================================

    public async Task<Camp> CreateCampAsync(
        Guid createdByUserId, string name, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        CampSeasonData seasonData, List<string>? historicalNames, int year,
        CancellationToken cancellationToken = default)
    {
        var camp = await WithInner(inner => inner.CreateCampAsync(
            createdByUserId, name, contactEmail, contactPhone, webOrSocialUrl,
            links, isSwissCamp, timesAtNowhere, seasonData, historicalNames, year,
            cancellationToken));
        await InvalidateCampAsync(camp.Id, cancellationToken);
        return camp;
    }

    public async Task<CampSeason> OptInToSeasonAsync(
        Guid campId, int year, CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.OptInToSeasonAsync(campId, year, cancellationToken));
        await InvalidateCampAsync(campId, cancellationToken);
        return result;
    }

    public async Task UpdateSeasonAsync(
        Guid seasonId, CampSeasonData data, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.UpdateSeasonAsync(seasonId, data, cancellationToken));
        await InvalidateBySeasonAsync(seasonId, cancellationToken);
    }

    public async Task ApproveSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.ApproveSeasonAsync(seasonId, reviewedByUserId, notes, cancellationToken));
        await InvalidateBySeasonAsync(seasonId, cancellationToken);
    }

    public async Task RejectSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.RejectSeasonAsync(seasonId, reviewedByUserId, notes, cancellationToken));
        await InvalidateBySeasonAsync(seasonId, cancellationToken);
    }

    public async Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.WithdrawSeasonAsync(seasonId, cancellationToken));
        await InvalidateBySeasonAsync(seasonId, cancellationToken);
    }

    public async Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.ReactivateSeasonAsync(seasonId, cancellationToken));
        await InvalidateBySeasonAsync(seasonId, cancellationToken);
    }

    public async Task<CampUpdateResult> UpdateCampAsync(
        CampUpdateInput input, CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.UpdateCampAsync(input, cancellationToken));
        if (result.Succeeded)
            await InvalidateCampAsync(input.CampId, cancellationToken);
        return result;
    }

    public async Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.DeleteCampAsync(campId, cancellationToken));
        // Confirmed delete — tombstone the key without flipping warmth so the
        // all-rows invariant (now N-1 entries) is preserved.
        DeleteKey(campId);
    }

    public async Task<CampLead> AddLeadAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.AddLeadAsync(campId, userId, cancellationToken));
        await InvalidateCampAsync(campId, cancellationToken);
        return result;
    }

    public async Task RemoveLeadAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        // RemoveLead doesn't return campId, so we evict the lead's camp by
        // scanning the snapshot. Fallback: full reload on miss.
        var campId = FindCampIdByLeadId(leadId);
        await WithInner(inner => inner.RemoveLeadAsync(leadId, cancellationToken));
        if (campId is not null)
            await InvalidateCampAsync(campId.Value, cancellationToken);
        else
            RefreshAll();
    }

    public async Task AddHistoricalNameAsync(
        Guid campId, string name, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.AddHistoricalNameAsync(campId, name, cancellationToken));
        await InvalidateCampAsync(campId, cancellationToken);
    }

    public async Task RemoveHistoricalNameAsync(
        Guid historicalNameId, CancellationToken cancellationToken = default)
    {
        // No FK back to the camp on the API surface — refresh all rather than
        // risk a stale entry. Historical-name churn is rare.
        await WithInner(inner => inner.RemoveHistoricalNameAsync(historicalNameId, cancellationToken));
        RefreshAll();
    }

    public async Task<CampImageUploadResult> UploadImageAsync(
        Guid campId, Stream fileStream, string fileName, string contentType, long length,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.UploadImageAsync(
            campId, fileStream, fileName, contentType, length, cancellationToken));
        if (result.Succeeded)
            await InvalidateCampAsync(campId, cancellationToken);
        return result;
    }

    public async Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        // Image rows hold CampId server-side; delegate first then full-refresh
        // since we don't have a snapshot index by image id. Image churn is rare.
        await WithInner(inner => inner.DeleteImageAsync(imageId, cancellationToken));
        RefreshAll();
    }

    public async Task ReorderImagesAsync(
        Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.ReorderImagesAsync(campId, imageIdsInOrder, cancellationToken));
        await InvalidateCampAsync(campId, cancellationToken);
    }

    public async Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.SetPublicYearAsync(year, cancellationToken));
        await InvalidateSettingsAsync(cancellationToken);
    }

    public async Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.OpenSeasonAsync(year, cancellationToken));
        await InvalidateSettingsAsync(cancellationToken);
    }

    public async Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.CloseSeasonAsync(year, cancellationToken));
        await InvalidateSettingsAsync(cancellationToken);
    }

    public async Task SetNameLockDateAsync(
        int year, LocalDate lockDate, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.SetNameLockDateAsync(year, lockDate, cancellationToken));
        // Touches every season in the year — drop the dict and let the next
        // read drive a fresh WarmAllAsync via the base.
        RefreshAll();
    }

    public async Task ChangeSeasonNameAsync(
        Guid seasonId, string newName, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.ChangeSeasonNameAsync(seasonId, newName, cancellationToken));
        await InvalidateBySeasonAsync(seasonId, cancellationToken);
    }

    // Membership writes — every one invalidates the season's parent camp
    // because EeGrantedCount + season MemberCount can move.

    public async Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.RequestCampMembershipAsync(campId, userId, cancellationToken));
        await InvalidateCampAsync(campId, cancellationToken);
        return result;
    }

    public async Task ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.ApproveCampMemberAsync(scopedCampId, campMemberId, approvedByUserId, cancellationToken));
        await InvalidateCampAsync(scopedCampId, cancellationToken);
    }

    public async Task RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.RejectCampMemberAsync(scopedCampId, campMemberId, rejectedByUserId, cancellationToken));
        await InvalidateCampAsync(scopedCampId, cancellationToken);
    }

    public async Task RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.RemoveCampMemberAsync(scopedCampId, campMemberId, removedByUserId, cancellationToken));
        await InvalidateCampAsync(scopedCampId, cancellationToken);
    }

    public async Task<AddCampMemberAsLeadResult> AddCampMemberToActiveSeasonAsLeadAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.AddCampMemberToActiveSeasonAsLeadAsync(campId, userId, actorUserId, cancellationToken));
        await InvalidateCampAsync(campId, cancellationToken);
        return result;
    }

    public async Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.AddMemberAndAssignRoleInActiveSeasonAsync(
            campId, roleDefinitionId, userId, actorUserId, cancellationToken));
        await InvalidateCampAsync(campId, cancellationToken);
        return result;
    }

    public async Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Member's camp is not in the API surface — match RemoveHistoricalNameAsync /
        // DeleteImageAsync and fall back to RefreshAll. Membership churn is rare.
        await WithInner(inner => inner.WithdrawCampMembershipRequestAsync(campMemberId, userId, cancellationToken));
        RefreshAll();
    }

    public async Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Leaving an Active member with HasEarlyEntry = true moves
        // CampSeasonInfo.EeGrantedCount; campId not on the API surface, so
        // RefreshAll. Guard on Succeeded so a failed precondition (member not
        // Active, etc.) doesn't trigger an unnecessary full re-warm.
        var result = await WithInner(inner => inner.LeaveCampAsync(campMemberId, userId, cancellationToken));
        if (result.Succeeded)
            RefreshAll();
        return result;
    }

    public async Task SetEeStartDateAsync(
        LocalDate? eeStartDate, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.SetEeStartDateAsync(eeStartDate, actorUserId, cancellationToken));
        await InvalidateSettingsAsync(cancellationToken);
    }

    public async Task SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.SetCampSeasonEeSlotCountAsync(campSeasonId, slotCount, actorUserId, cancellationToken));
        await InvalidateBySeasonAsync(campSeasonId, cancellationToken);
    }

    public async Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
        Guid scopedCampId, Guid campMemberId, bool granted, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.SetEarlyEntryAsync(
            scopedCampId, campMemberId, granted, actorUserId, cancellationToken));
        // EeGrantedCount projection moves on every grant/revoke.
        await InvalidateCampAsync(scopedCampId, cancellationToken);
        return result;
    }

    // ==========================================================================
    // IUserMerge — pass-through, then full refresh (lead reassignment can
    // touch any camp; a 100-camp full reload is cheap).
    // ==========================================================================

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        await WithInnerMerge(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));
        // Lead reassignment can touch any camp; drop the dict so the next read
        // re-warms from scratch (cheap at ~100 camps).
        RefreshAll();
    }

    // ==========================================================================
    // ICampInfoInvalidator
    // ==========================================================================

    /// <inheritdoc cref="ICampInfoInvalidator.InvalidateCampAsync" />
    public Task InvalidateCampAsync(Guid campId, CancellationToken ct = default) =>
        InvalidateCampAsync(campId, ct, memberName: string.Empty, filePath: string.Empty);

    private Task InvalidateCampAsync(
        Guid campId,
        CancellationToken ct,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "CampInfo invalidate campId={CampId} caller={CallerMember} file={CallerFile}",
            campId, memberName, Path.GetFileName(filePath));
        return RefreshEntryAsync(campId, ct);
    }

    /// <inheritdoc cref="ICampInfoInvalidator.InvalidateSettingsAsync" />
    public async Task InvalidateSettingsAsync(CancellationToken ct = default)
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

    // ==========================================================================
    // Warmup / refresh
    // ==========================================================================

    /// <summary>
    /// Populates the per-camp dict. Called by
    /// <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/> at startup (via
    /// the base's <see cref="IHostedService.StartAsync"/> when
    /// <c>warmOnStartup: true</c>) and again on demand after a <see cref="TrackedCache{TKey,TValue}.Clear"/>
    /// flips the warmed flag back to false. The base owns concurrency coalescing
    /// via the warm semaphore, so this body is invoked at most once at a time.
    ///
    /// <para>Iterates years from settings (PublicYear + OpenSeasons) plus the
    /// current real-world year so the projection carries seasons for every
    /// active year, not just the public year. At ~100 camps × ~1–3 active years
    /// this is one query per year, well under a second.</para>
    /// </summary>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(ct);
        var years = new HashSet<int>();
        if (settings is not null)
        {
            years.Add(settings.PublicYear);
            foreach (var y in settings.OpenSeasons) years.Add(y);
        }
        // Always include the current real-world year so historical seasons
        // for camps that never opted into a future year still surface.
        years.Add(SystemClockYear());

        var byCampId = new Dictionary<Guid, Camp>();
        foreach (var year in years)
        {
            var camps = await _repo.GetCampsWithLeadsForYearAsync(year, statusFilter: null, ct);
            foreach (var camp in camps)
            {
                if (byCampId.TryGetValue(camp.Id, out var existing))
                {
                    // Merge seasons from this year into the existing entry.
                    foreach (var s in camp.Seasons)
                    {
                        if (!existing.Seasons.Any(es => es.Id == s.Id))
                            existing.Seasons.Add(s);
                    }
                }
                else
                {
                    byCampId[camp.Id] = camp;
                }
            }
        }

        // No defensive Clear() — the base already emptied the dict before
        // flipping the warmed flag (Clear path) or the cache is empty on
        // first startup. Set is upsert, so any rare leftover is overwritten.
        foreach (var (campId, camp) in byCampId)
        {
            Set(campId, ProjectCampInfo(camp));
        }

        // Publish the warm year set so GetCampsForYearAsync can detect
        // cold-year requests and fall back to the inner service.
        _warmYears = years;

        // Populate the settings slot from the value already fetched at the
        // top of this method — avoids a second repo round-trip and removes
        // the boot-time throw LoadSettingsAsync carries (no-startup-guards).
        // If settings is null the slot stays unset and GetSettingsAsync
        // lazy-loads on first request.
        if (settings is not null)
        {
            await _settingsLock.WaitAsync(ct);
            try
            {
                _settings = new CampSettingsInfo(
                    settings.PublicYear,
                    settings.OpenSeasons.ToList(),
                    settings.EeStartDate);
            }
            finally
            {
                _settingsLock.Release();
            }
        }
    }

    private int SystemClockYear() => _clock.GetCurrentInstant().InUtc().Year;

    private async Task RefreshEntryAsync(Guid campId, CancellationToken ct)
    {
        // Per-row replace preserving the all-rows invariant: if the row is
        // gone the cache tombstones it via DeleteKey (warmth stays true);
        // otherwise we Set the new projection. Never Invalidate(key) here —
        // that would flip warmth on this warmOnStartup:true cache and force
        // a full re-warm on the next read.
        var camp = await _repo.GetByIdAsync(campId, ct);
        if (camp is null)
        {
            DeleteKey(campId);
            return;
        }
        Set(campId, ProjectCampInfo(camp));
    }

    /// <summary>
    /// Drop the cached dict and let the next read trigger a fresh
    /// <see cref="WarmAllAsync"/>. The base's <see cref="TrackedCache{TKey,TValue}.Clear"/>
    /// flips the warmed flag back to false; load-all readers observe that and
    /// re-warm via <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/>.
    /// </summary>
    private void RefreshAll()
    {
        // Drop the warm-years marker too so a cold-year request between Clear
        // and the next WarmAllAsync correctly falls back to the inner service
        // rather than reading a stale "warm" claim against an empty dict.
        _warmYears = null;
        Clear();
    }

    private async Task<CampSettingsInfo> LoadSettingsAsync(CancellationToken ct)
    {
        await _settingsLock.WaitAsync(ct);
        try
        {
            if (_settings is not null) return _settings;
            var entity = await _repo.GetSettingsReadOnlyAsync(ct);
            if (entity is null)
                throw new InvalidOperationException("Camp settings not found.");
            _settings = new CampSettingsInfo(
                entity.PublicYear,
                entity.OpenSeasons.ToList(),
                entity.EeStartDate);
            return _settings;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task InvalidateBySeasonAsync(Guid seasonId, CancellationToken ct)
    {
        // Look up the season's camp from the cache snapshot first to avoid
        // an unnecessary DB round-trip.
        foreach (var camp in Values)
        {
            if (camp.Seasons.Any(s => s.Id == seasonId))
            {
                await InvalidateCampAsync(camp.Id, ct);
                return;
            }
        }
        // Cache miss — ask repo for the season's camp id directly.
        var season = await _repo.GetSeasonByIdAsync(seasonId, ct);
        if (season is not null)
            await InvalidateCampAsync(season.CampId, ct);
    }

    private Guid? FindCampIdByLeadId(Guid leadId)
    {
        foreach (var camp in Values)
        {
            if (camp.Leads.Any(l => l.Id == leadId)) return camp.Id;
        }
        return null;
    }

    // ==========================================================================
    // Projection + filter helpers
    // ==========================================================================

    private static CampInfo ProjectCampInfo(Camp camp) => new(
        camp.Id,
        camp.Slug,
        camp.ContactEmail,
        camp.ContactPhone,
        camp.IsSwissCamp,
        camp.TimesAtNowhere,
        camp.Seasons.Select(s => ProjectSeasonInfo(s, camp.Slug)).ToList(),
        camp.Leads.Where(l => l.LeftAt is null).Select(l => new CampLeadInfo(l.Id, l.UserId)).ToList());

    private static CampSeasonInfo ProjectSeasonInfo(CampSeason season, string campSlug) => new(
        season.Id,
        season.CampId,
        campSlug,
        season.Year,
        season.NameLockDate,
        season.Name,
        season.BlurbShort,
        season.Languages,
        season.Vibes.ToList(),
        season.Status,
        season.AcceptingMembers,
        season.KidsWelcome,
        season.AdultPlayspace,
        season.MemberCount,
        season.SoundZone,
        season.SpaceRequirement,
        season.ElectricalGrid,
        season.EeSlotCount,
        season.Members is { Count: > 0 }
            ? season.Members.Count(m => m.Status == CampMemberStatus.Active && m.HasEarlyEntry)
            : 0);

    private static CampInfo FilterToYear(CampInfo camp, int year) =>
        camp with
        {
            Seasons = camp.Seasons.Where(s => s.Year == year).ToList()
        };

    // ==========================================================================
    // Scope / inner resolution
    // ==========================================================================

    private async Task<T> WithInner<T>(Func<ICampService, Task<T>> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampService>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInner(Func<ICampService, Task> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampService>(InnerServiceKey);
        await work(inner);
    }

    private async Task WithInnerMerge(Func<IUserMerge, Task> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserMerge>(InnerServiceKey);
        await work(inner);
    }
}
