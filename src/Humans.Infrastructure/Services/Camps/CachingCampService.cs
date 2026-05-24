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
/// Singleton caching decorator for <see cref="ICampService"/>. Per-camp
/// <see cref="CampInfo"/> dict plus a single-slot <see cref="CampSettingsInfo"/>.
/// Year-keyed reads are filtered snapshots, not separate cache entries.
/// </summary>
public sealed class CachingCampService(
    ICampRepository repo,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<CachingCampService> logger) : TrackedCache<Guid, CampInfo>("Camp.CampInfo", warmOnStartup: true, logger),
    ICampService, IUserMerge, ICampInfoInvalidator
{
    /// <summary>DI key for the undecorated inner <see cref="ICampService"/>.</summary>
    public const string InnerServiceKey = "camp-inner";

    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private CampSettingsInfo? _settings;
    // Years populated by warmup; cold-year reads fall back to the inner service.
    private volatile IReadOnlySet<int>? _warmYears;

    // Cached reads

    public Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampBySlugAsync(slug, cancellationToken));

    public async Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);
        // Cold year — fall back to inner service so admin/year-driven flows
        // that pass arbitrary years still get correct results.
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

    public async Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _settings;
        if (snapshot is not null) return snapshot;
        return await LoadSettingsAsync(cancellationToken);
    }

    // Lead authz delegates straight to the inner service — the source of truth
    // is CampRoleAssignment (SpecialRole = Lead), not the CampInfo projection.
    public Task<bool> IsUserCampLeadAsync(
        Guid userId, Guid campId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.IsUserCampLeadAsync(userId, campId, cancellationToken));

    public Task<bool> IsUserCampEventManagerAsync(
        Guid userId, Guid campId, CancellationToken cancellationToken = default) =>
        // Delegate so the Lead OR Workshop check sees the role-assignment table directly.
        WithInner(inner => inner.IsUserCampEventManagerAsync(userId, campId, cancellationToken));

    public Task<IReadOnlyList<CampInfo>> GetEventManagedCampsAsync(
        Guid userId, int year, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetEventManagedCampsAsync(userId, year, cancellationToken));

    // Same rationale as IsUserCampLeadAsync above.
    public Task<Guid?> GetCampLeadSeasonIdForYearAsync(
        Guid userId, int year, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampLeadSeasonIdForYearAsync(userId, year, cancellationToken));

    // Pass-through reads

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

    public Task<CampSeasonInfo?> GetCampSeasonByIdAsync(
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

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CampSeasonMemberInfo>>> GetCampMembersByYearAsync(
        int year, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCampMembersByYearAsync(year, cancellationToken));

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

    // Writes — delegate then invalidate

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
        // Tombstone without flipping warmth — preserves the all-rows invariant.
        DeleteKey(campId);
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
        // No campId on the API surface; historical-name churn is rare — RefreshAll.
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
        // No campId on the API surface; image churn is rare — RefreshAll.
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
        // Touches every season in the year — RefreshAll.
        RefreshAll();
    }

    public async Task ChangeSeasonNameAsync(
        Guid seasonId, string newName, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.ChangeSeasonNameAsync(seasonId, newName, cancellationToken));
        await InvalidateBySeasonAsync(seasonId, cancellationToken);
    }

    // Membership writes — invalidate the parent camp (EeGrantedCount + MemberCount move).

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

    public async Task<Guid> EnsureActiveMemberForMigrationAsync(
        Guid campSeasonId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var memberId = await WithInner(inner => inner.EnsureActiveMemberForMigrationAsync(
            campSeasonId, userId, actorUserId, cancellationToken));
        await InvalidateBySeasonAsync(campSeasonId, cancellationToken);
        return memberId;
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
        // No campId on the API surface — RefreshAll.
        await WithInner(inner => inner.WithdrawCampMembershipRequestAsync(campMemberId, userId, cancellationToken));
        RefreshAll();
    }

    public async Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        // No campId on the API surface; can move EeGrantedCount — RefreshAll on success.
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
        await InvalidateCampAsync(scopedCampId, cancellationToken);
        return result;
    }

    // IUserMerge

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        await WithInnerMerge(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));
        // Lead reassignment can touch any camp — RefreshAll.
        RefreshAll();
    }

    // ICampInfoInvalidator

    /// <inheritdoc cref="ICampInfoInvalidator.InvalidateCampAsync" />
    public Task InvalidateCampAsync(Guid campId, CancellationToken ct = default) =>
        InvalidateCampAsync(campId, ct, memberName: string.Empty, filePath: string.Empty);

    private Task InvalidateCampAsync(
        Guid campId,
        CancellationToken ct,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        logger.LogDebug(
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

    // Warmup / refresh

    /// <summary>
    /// Populates the per-camp dict from PublicYear ∪ OpenSeasons ∪ currentYear.
    /// </summary>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var settings = await repo.GetSettingsReadOnlyAsync(ct);
        var years = new HashSet<int>();
        if (settings is not null)
        {
            years.Add(settings.PublicYear);
            foreach (var y in settings.OpenSeasons) years.Add(y);
        }
        years.Add(SystemClockYear());

        var byCampId = new Dictionary<Guid, Camp>();
        foreach (var year in years)
        {
            var camps = await repo.GetCampsWithLeadsForYearAsync(year, statusFilter: null, ct);
            foreach (var camp in camps)
            {
                if (byCampId.TryGetValue(camp.Id, out var existing))
                {
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

        foreach (var (campId, camp) in byCampId)
        {
            Set(campId, ProjectCampInfo(camp));
        }

        _warmYears = years;

        // Reuse the settings fetched above; null means lazy-load on first request.
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

    private int SystemClockYear() => clock.GetCurrentInstant().InUtc().Year;

    private async Task RefreshEntryAsync(Guid campId, CancellationToken ct)
    {
        // Per-row replace; preserves the all-rows invariant. Never Invalidate(key)
        // here — it would flip warmth and force a full re-warm.
        var camp = await repo.GetByIdAsync(campId, ct);
        if (camp is null)
        {
            DeleteKey(campId);
            return;
        }
        Set(campId, ProjectCampInfo(camp));
    }

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
            var entity = await repo.GetSettingsReadOnlyAsync(ct);
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
        // Try the snapshot first to avoid a DB round-trip.
        foreach (var camp in Values)
        {
            if (camp.Seasons.Any(s => s.Id == seasonId))
            {
                await InvalidateCampAsync(camp.Id, ct);
                return;
            }
        }
        var season = await repo.GetSeasonByIdAsync(seasonId, ct);
        if (season is not null)
            await InvalidateCampAsync(season.CampId, ct);
    }

    // Projection + filter helpers

    private static CampInfo ProjectCampInfo(Camp camp) => new(
        camp.Id,
        camp.Slug,
        camp.ContactEmail,
        camp.ContactPhone,
        camp.IsSwissCamp,
        camp.TimesAtNowhere,
        camp.Seasons.Select(s => ProjectSeasonInfo(s, camp.Slug)).ToList());

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
            : 0,
        season.Members is { Count: > 0 }
            ? season.Members.Count(m => m.Status == CampMemberStatus.Active)
            : 0);

    private static CampInfo FilterToYear(CampInfo camp, int year) =>
        camp with
        {
            Seasons = camp.Seasons.Where(s => s.Year == year).ToList()
        };

    // Scope / inner resolution

    private async Task<T> WithInner<T>(Func<ICampService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampService>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInner(Func<ICampService, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ICampService>(InnerServiceKey);
        await work(inner);
    }

    private async Task WithInnerMerge(Func<IUserMerge, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserMerge>(InnerServiceKey);
        await work(inner);
    }
}
