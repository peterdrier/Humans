using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Camps;

/// <summary>
/// Application-layer implementation of <see cref="ICampService"/>. Goes
/// through <see cref="ICampRepository"/> for all data access — this type
/// never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </summary>
/// <remarks>
/// Cross-section interactions:
/// <list type="bullet">
///   <item><see cref="IUserService"/> — resolves lead display names for
///     DTOs that previously loaded them via <c>CampLead.User</c>
///     cross-domain nav (design-rules §6).</item>
///   <item><see cref="ISystemTeamSync"/> — the Barrio Leads system team
///     membership is kept in sync after lead mutations.</item>
///   <item><see cref="ICampImageStorage"/> — infrastructure concern that
///     owns disk writes for uploaded images.</item>
/// </list>
/// Caching: short-TTL <see cref="IMemoryCache"/> is used for "camps for
/// year" and "camp settings" reads (<c>~5 min</c>). At ~100 camps these
/// are request-acceleration caches, not canonical domain caches, so §15
/// transparent-cache rules do not apply (see design-rules §15f).
/// </remarks>
public sealed class CampService : ICampService, IUserDataContributor
{
    private readonly ICampRepository _repo;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLog;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly ICampImageStorage _imageStorage;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly ICampLeadJoinRequestsBadgeCacheInvalidator _leadBadgeInvalidator;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CampService> _logger;

    private static readonly TimeSpan CampsForYearCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CampSettingsCacheTtl = TimeSpan.FromMinutes(5);

    public CampService(
        ICampRepository repo,
        IUserService userService,
        IAuditLogService auditLog,
        ISystemTeamSync systemTeamSync,
        ICampImageStorage imageStorage,
        INotificationEmitter notificationEmitter,
        ICampLeadJoinRequestsBadgeCacheInvalidator leadBadgeInvalidator,
        IClock clock,
        IMemoryCache cache,
        ILogger<CampService> logger)
    {
        _repo = repo;
        _userService = userService;
        _auditLog = auditLog;
        _systemTeamSync = systemTeamSync;
        _imageStorage = imageStorage;
        _notificationEmitter = notificationEmitter;
        _leadBadgeInvalidator = leadBadgeInvalidator;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    // ==========================================================================
    // Registration
    // ==========================================================================

    public async Task<Camp> CreateCampAsync(
        Guid createdByUserId, string name, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        CampSeasonData seasonData, List<string>? historicalNames, int year,
        CancellationToken cancellationToken = default)
    {
        var slug = SlugHelper.GenerateSlug(name);
        if (SlugHelper.IsReservedCampSlug(slug))
        {
            throw new InvalidOperationException($"The name '{name}' generates a reserved slug.");
        }

        // Ensure unique slug
        var baseSlug = slug;
        var suffix = 2;
        while (await _repo.SlugExistsAsync(slug, cancellationToken))
        {
            slug = $"{baseSlug}-{suffix}";
            suffix++;
        }

        var now = _clock.GetCurrentInstant();
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            ContactEmail = contactEmail,
            ContactPhone = contactPhone,
            WebOrSocialUrl = links is { Count: > 0 } ? null : webOrSocialUrl,
            Links = links,
            IsSwissCamp = isSwissCamp,
            TimesAtNowhere = timesAtNowhere,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        var season = CreateSeasonFromData(camp.Id, year, name, seasonData, now);

        var lead = new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            UserId = createdByUserId,
            Role = CampLeadRole.CoLead,
            JoinedAt = now
        };

        List<CampHistoricalName>? historicalNameEntities = null;
        if (historicalNames is { Count: > 0 })
        {
            historicalNameEntities = historicalNames.Select(oldName => new CampHistoricalName
            {
                Id = Guid.NewGuid(),
                CampId = camp.Id,
                Name = oldName,
                Source = CampNameSource.Manual,
                CreatedAt = now
            }).ToList();
        }

        await _repo.CreateCampAsync(camp, season, lead, historicalNameEntities, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampCreated, nameof(Camp), camp.Id,
            $"Registered camp '{name}' for {year}",
            createdByUserId);

        await _systemTeamSync.SyncBarrioLeadsMembershipForUserAsync(createdByUserId, cancellationToken);
        InvalidateCache(year);

        return camp;
    }

    // ==========================================================================
    // Queries
    // ==========================================================================

    public Task<Camp?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        _repo.GetBySlugAsync(slug, cancellationToken);

    public Task<Camp?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default) =>
        _repo.GetByIdAsync(campId, cancellationToken);

    public async Task<CampDetailData?> GetCampDetailAsync(
        string slug,
        int? preferredYear = null,
        bool fallbackToLatestSeason = true,
        CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetBySlugAsync(slug, cancellationToken);
        if (camp is null)
        {
            return null;
        }

        var targetYear = preferredYear;
        if (!targetYear.HasValue)
        {
            var settings = await GetSettingsAsync(cancellationToken);
            targetYear = settings.PublicYear;
        }

        var season = camp.Seasons
            .Where(s => s.Year == targetYear.Value)
            .OrderByDescending(s => s.Year)
            .FirstOrDefault();

        if (season is null && fallbackToLatestSeason)
        {
            season = camp.Seasons
                .OrderByDescending(s => s.Year)
                .FirstOrDefault();
        }

        if (season is null)
        {
            return null;
        }

        var leadSummaries = await BuildLeadSummariesAsync(camp.Leads, cancellationToken);

        return new CampDetailData(
            camp.Id,
            camp.Slug,
            season.Name,
            CreateCampLinks(camp),
            camp.IsSwissCamp,
            camp.TimesAtNowhere,
            camp.HideHistoricalNames,
            camp.HistoricalNames.Select(h => h.Name).ToList(),
            camp.Images.OrderBy(i => i.SortOrder).Select(i => $"/{i.StoragePath}").ToList(),
            leadSummaries,
            CreateCampSeasonDetailData(season));
    }

    public async Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        int? preferredYear = null,
        CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null)
        {
            return null;
        }

        var targetYear = preferredYear;
        if (!targetYear.HasValue)
        {
            var settings = await GetSettingsAsync(cancellationToken);
            targetYear = settings.PublicYear;
        }

        var season = camp.Seasons
            .Where(s => s.Year == targetYear.Value)
            .OrderByDescending(s => s.Year)
            .FirstOrDefault()
            ?? camp.Seasons
                .OrderByDescending(s => s.Year)
                .FirstOrDefault();

        if (season is null)
        {
            return null;
        }

        var leadSummaries = await BuildLeadSummariesAsync(camp.Leads, cancellationToken);
        return CreateCampEditData(camp, season, leadSummaries);
    }

    public async Task<CampDirectoryResult> GetCampDirectoryAsync(
        Guid? userId,
        CampDirectoryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var year = settings.PublicYear;
        var camps = await GetCampsForYearAsync(year, cancellationToken);

        var cards = ApplyCampDirectoryFilter(
            camps.Select(camp => CreateCampDirectoryCard(camp, year)),
            filter)
            .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var myCamps = new List<CampDirectoryCard>();
        if (userId.HasValue)
        {
            var leadCamps = await _repo.GetCampsByLeadUserIdAsync(userId.Value, cancellationToken);
            myCamps = leadCamps
                .Where(camp => camp.Seasons.Any(season =>
                    season.Year == year &&
                    season.Status != CampSeasonStatus.Active &&
                    season.Status != CampSeasonStatus.Full))
                .Where(camp => cards.All(card => card.Id != camp.Id))
                .Select(camp => CreateCampDirectoryCard(camp, year))
                .ToList();
        }

        var pendingCount = await _repo.CountPendingSeasonsAsync(cancellationToken);

        return new CampDirectoryResult(year, pendingCount, cards, myCamps);
    }

    public async Task<List<Camp>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetOrCreateAsync(
            CacheKeys.CampSeasonsByYear(year),
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CampsForYearCacheTtl;
                return await _repo.GetPublicCampsForYearAsync(year, cancellationToken);
            });

        return cached is null ? new List<Camp>() : cached.ToList();
    }

    public async Task<List<Camp>> GetAllCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var camps = await _repo.GetAllCampsForYearAsync(year, cancellationToken);
        return camps.ToList();
    }

    public async Task<IReadOnlyList<CampPublicSummary>> GetCampPublicSummariesForYearAsync(
        int year,
        CancellationToken cancellationToken = default)
    {
        var camps = await GetCampsForYearAsync(year, cancellationToken);

        return camps
            .Select(camp => CreateCampPublicSummary(camp, year))
            .OrderBy(camp => camp.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<CampPlacementSummary>> GetCampPlacementSummariesForYearAsync(
        int year,
        CancellationToken cancellationToken = default)
    {
        var camps = await GetCampsForYearAsync(year, cancellationToken);

        return camps
            .Select(camp => CreateCampPlacementSummary(camp, year))
            .Where(summary => summary is not null)
            .Select(summary => summary!)
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<Camp>> GetCampsWithLeadsForYearAsync(
        int year,
        IReadOnlyList<CampSeasonStatus>? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        var camps = await _repo.GetCampsWithLeadsForYearAsync(
            year, statusFilter, cancellationToken);
        return camps.ToList();
    }

    public async Task<List<Camp>> GetCampsByLeadUserIdAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var camps = await _repo.GetCampsByLeadUserIdAsync(userId, cancellationToken);
        return camps.ToList();
    }

    public async Task<CampSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetOrCreateAsync(
            CacheKeys.CampSettings,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CampSettingsCacheTtl;
                return await _repo.GetSettingsReadOnlyAsync(cancellationToken);
            });

        return cached ?? throw new InvalidOperationException("Camp settings not found.");
    }

    public async Task<List<CampSeason>> GetPendingSeasonsAsync(CancellationToken cancellationToken = default)
    {
        var seasons = await _repo.GetPendingSeasonsAsync(cancellationToken);
        return seasons.ToList();
    }

    private static IEnumerable<CampDirectoryCard> ApplyCampDirectoryFilter(
        IEnumerable<CampDirectoryCard> camps,
        CampDirectoryFilter? filter)
    {
        if (filter?.Vibe.HasValue == true)
        {
            camps = camps.Where(card => card.Vibes.Contains(filter.Vibe.Value));
        }

        if (filter?.SoundZone.HasValue == true)
        {
            camps = camps.Where(card => card.SoundZone == filter.SoundZone.Value);
        }

        if (filter?.KidsFriendly == true)
        {
            camps = camps.Where(card => card.KidsWelcome == YesNoMaybe.Yes);
        }

        if (filter?.AcceptingMembers == true)
        {
            camps = camps.Where(card => card.AcceptingMembers == YesNoMaybe.Yes);
        }

        return camps;
    }

    private static CampDirectoryCard CreateCampDirectoryCard(Camp camp, int year)
    {
        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        var firstImage = camp.Images.OrderBy(i => i.SortOrder).FirstOrDefault();

        return new CampDirectoryCard(
            camp.Id,
            camp.Slug,
            season?.Name ?? camp.Slug,
            season?.BlurbShort ?? string.Empty,
            firstImage is not null ? $"/{firstImage.StoragePath}" : null,
            season?.Vibes ?? [],
            season?.AcceptingMembers ?? YesNoMaybe.No,
            season?.KidsWelcome ?? YesNoMaybe.No,
            season?.SoundZone,
            season?.Status ?? CampSeasonStatus.Pending,
            camp.TimesAtNowhere);
    }

    private static IReadOnlyList<CampLink> CreateCampLinks(Camp camp)
    {
        if (camp.Links is { Count: > 0 })
        {
            return camp.Links;
        }

        return camp.WebOrSocialUrl is not null
            ? [new CampLink { Url = camp.WebOrSocialUrl }]
            : [];
    }

    private async Task<IReadOnlyList<CampLeadSummary>> BuildLeadSummariesAsync(
        IEnumerable<CampLead> leads,
        CancellationToken cancellationToken)
    {
        var activeLeads = leads.Where(l => l.IsActive).ToList();
        if (activeLeads.Count == 0)
        {
            return [];
        }

        var userIds = activeLeads.Select(l => l.UserId).Distinct().ToList();
        var users = await _userService.GetByIdsAsync(userIds, cancellationToken);

        return activeLeads
            .Select(l => new CampLeadSummary(
                l.Id,
                l.UserId,
                users.TryGetValue(l.UserId, out var user) ? user.DisplayName : string.Empty))
            .ToList();
    }

    private CampSeasonDetailData CreateCampSeasonDetailData(CampSeason season)
    {
        var today = _clock.GetCurrentInstant().InUtc().Date;

        return new CampSeasonDetailData(
            season.Id,
            season.Year,
            season.Name,
            season.Status,
            season.BlurbLong,
            season.BlurbShort,
            season.Languages,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.KidsVisiting,
            season.KidsAreaDescription,
            season.HasPerformanceSpace,
            season.PerformanceTypes,
            season.Vibes.ToList(),
            season.AdultPlayspace,
            season.MemberCount,
            season.SpaceRequirement,
            season.SoundZone,
            season.ContainerCount,
            season.ContainerNotes,
            season.ElectricalGrid,
            season.NameLockDate.HasValue && today >= season.NameLockDate.Value);
    }

    private CampEditData CreateCampEditData(Camp camp, CampSeason season, IReadOnlyList<CampLeadSummary> leads)
    {
        var today = _clock.GetCurrentInstant().InUtc().Date;

        return new CampEditData(
            camp.Id,
            camp.Slug,
            season.Id,
            season.Year,
            season.NameLockDate.HasValue && today >= season.NameLockDate.Value,
            season.Name,
            camp.ContactEmail,
            camp.ContactPhone,
            camp.Links is { Count: > 0 }
                ? camp.Links.Select(l => l.Url).ToList()
                : camp.WebOrSocialUrl is not null
                    ? [camp.WebOrSocialUrl]
                    : [],
            camp.IsSwissCamp,
            camp.HideHistoricalNames,
            camp.TimesAtNowhere,
            season.BlurbLong,
            season.BlurbShort,
            season.Languages,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.KidsVisiting,
            season.KidsAreaDescription,
            season.HasPerformanceSpace,
            season.PerformanceTypes,
            season.Vibes.ToList(),
            season.AdultPlayspace,
            season.MemberCount,
            season.SpaceRequirement,
            season.SoundZone,
            season.ContainerCount,
            season.ContainerNotes,
            season.ElectricalGrid,
            leads,
            camp.Images
                .OrderBy(i => i.SortOrder)
                .Select(i => new CampImageSummary(i.Id, $"/{i.StoragePath}", i.SortOrder))
                .ToList(),
            camp.HistoricalNames
                .Select(h => new CampHistoricalNameSummary(h.Id, h.Name, h.Year, h.Source.ToString()))
                .ToList());
    }

    private static CampPublicSummary CreateCampPublicSummary(Camp camp, int year)
    {
        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        var firstImage = camp.Images.OrderBy(i => i.SortOrder).FirstOrDefault();

        return new CampPublicSummary(
            camp.Id,
            camp.Slug,
            season?.Name ?? camp.Slug,
            season?.BlurbShort ?? string.Empty,
            season?.BlurbLong ?? string.Empty,
            firstImage is not null ? $"/{firstImage.StoragePath}" : null,
            (season?.Vibes ?? []).Select(vibe => vibe.ToString()).ToList(),
            (season?.AcceptingMembers ?? YesNoMaybe.No).ToString(),
            (season?.KidsWelcome ?? YesNoMaybe.No).ToString(),
            season?.SoundZone?.ToString(),
            (season?.Status ?? CampSeasonStatus.Pending).ToString(),
            camp.TimesAtNowhere,
            camp.IsSwissCamp,
            camp.Links,
            camp.WebOrSocialUrl);
    }

    private static CampPlacementSummary? CreateCampPlacementSummary(Camp camp, int year)
    {
        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        if (season is null)
        {
            return null;
        }

        return new CampPlacementSummary(
            camp.Id,
            camp.Slug,
            season.Name,
            season.MemberCount,
            season.SpaceRequirement?.ToString(),
            season.SoundZone?.ToString(),
            season.ContainerCount,
            season.ContainerNotes,
            season.Status.ToString(),
            season.ElectricalGrid?.ToString());
    }

    // ==========================================================================
    // Season management
    // ==========================================================================

    public async Task<CampSeason> OptInToSeasonAsync(
        Guid campId, int year, CancellationToken cancellationToken = default)
    {
        // Verify season is open
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.OpenSeasons.Contains(year))
        {
            throw new InvalidOperationException($"Season {year} is not open for registration.");
        }

        // Check no existing season for this year
        if (await _repo.SeasonExistsAsync(campId, year, cancellationToken))
        {
            throw new InvalidOperationException($"Camp already has a season for {year}.");
        }

        // Copy from most recent season
        var previousSeason = await _repo.GetLatestSeasonAsync(campId, cancellationToken)
            ?? throw new InvalidOperationException("No previous season to copy from.");

        var hasApprovedSeason = await _repo.HasApprovedSeasonAsync(campId, cancellationToken);

        var now = _clock.GetCurrentInstant();
        var newSeason = new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Year = year,
            Name = previousSeason.Name,
            Status = hasApprovedSeason ? CampSeasonStatus.Active : CampSeasonStatus.Pending,
            BlurbLong = previousSeason.BlurbLong,
            BlurbShort = previousSeason.BlurbShort,
            Languages = previousSeason.Languages,
            AcceptingMembers = previousSeason.AcceptingMembers,
            KidsWelcome = previousSeason.KidsWelcome,
            KidsVisiting = previousSeason.KidsVisiting,
            KidsAreaDescription = previousSeason.KidsAreaDescription,
            HasPerformanceSpace = previousSeason.HasPerformanceSpace,
            PerformanceTypes = previousSeason.PerformanceTypes,
            Vibes = new List<CampVibe>(previousSeason.Vibes),
            AdultPlayspace = previousSeason.AdultPlayspace,
            MemberCount = previousSeason.MemberCount,
            SpaceRequirement = previousSeason.SpaceRequirement,
            SoundZone = previousSeason.SoundZone,
            ContainerCount = previousSeason.ContainerCount,
            ContainerNotes = previousSeason.ContainerNotes,
            ElectricalGrid = previousSeason.ElectricalGrid,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repo.AddSeasonAsync(newSeason, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampSeasonCreated, nameof(CampSeason), newSeason.Id,
            $"Opted in to season {year} (auto-approved: {hasApprovedSeason})",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        InvalidateCache(year);

        return newSeason;
    }

    public async Task UpdateSeasonAsync(
        Guid seasonId, CampSeasonData data, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            season.BlurbLong = data.BlurbLong;
            season.BlurbShort = data.BlurbShort;
            season.Languages = data.Languages;
            season.AcceptingMembers = data.AcceptingMembers;
            season.KidsWelcome = data.KidsWelcome;
            season.KidsVisiting = data.KidsVisiting;
            season.KidsAreaDescription = data.KidsAreaDescription;
            season.HasPerformanceSpace = data.HasPerformanceSpace;
            season.PerformanceTypes = data.PerformanceTypes;
            season.Vibes = new List<CampVibe>(data.Vibes);
            season.AdultPlayspace = data.AdultPlayspace;
            season.MemberCount = data.MemberCount;
            season.SpaceRequirement = data.SpaceRequirement;
            season.SoundZone = data.SoundZone;
            season.ContainerCount = data.ContainerCount;
            season.ContainerNotes = data.ContainerNotes;
            season.ElectricalGrid = data.ElectricalGrid;
            season.UpdatedAt = now;

            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (!found)
        {
            throw new InvalidOperationException("Season not found.");
        }

        await _auditLog.LogAsync(
            AuditAction.CampUpdated, nameof(CampSeason), seasonId,
            $"Updated season {year} details",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        InvalidateCache(year);
    }

    public async Task ApproveSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status != CampSeasonStatus.Pending)
            {
                throw new InvalidOperationException($"Cannot approve a season with status {season.Status}.");
            }

            season.Status = CampSeasonStatus.Active;
            season.ReviewedByUserId = reviewedByUserId;
            season.ReviewNotes = notes;
            season.ResolvedAt = now;
            season.UpdatedAt = now;

            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (!found)
        {
            throw new InvalidOperationException("Season not found.");
        }

        await _auditLog.LogAsync(
            AuditAction.CampSeasonApproved, nameof(CampSeason), seasonId,
            $"Approved season {year}",
            reviewedByUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        InvalidateCache(year);
    }

    public async Task RejectSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status != CampSeasonStatus.Pending)
            {
                throw new InvalidOperationException($"Cannot reject a season with status {season.Status}.");
            }

            season.Status = CampSeasonStatus.Rejected;
            season.ReviewedByUserId = reviewedByUserId;
            season.ReviewNotes = notes;
            season.ResolvedAt = now;
            season.UpdatedAt = now;

            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (!found)
        {
            throw new InvalidOperationException("Season not found.");
        }

        await _auditLog.LogAsync(
            AuditAction.CampSeasonRejected, nameof(CampSeason), seasonId,
            $"Rejected season {year}: {notes}",
            reviewedByUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await NotifyPendingRequestersOfSeasonClosureAsync(seasonId, campId, year, cancellationToken);

        InvalidateCache(year);
    }

    public async Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status != CampSeasonStatus.Pending && season.Status != CampSeasonStatus.Active)
            {
                throw new InvalidOperationException($"Cannot withdraw a season with status {season.Status}.");
            }

            season.Status = CampSeasonStatus.Withdrawn;
            season.UpdatedAt = now;

            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (!found)
        {
            throw new InvalidOperationException("Season not found.");
        }

        await _auditLog.LogAsync(
            AuditAction.CampSeasonWithdrawn, nameof(CampSeason), seasonId,
            $"Withdrew from season {year}",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await NotifyPendingRequestersOfSeasonClosureAsync(seasonId, campId, year, cancellationToken);

        InvalidateCache(year);
    }

    private async Task NotifyPendingRequestersOfSeasonClosureAsync(
        Guid seasonId, Guid campId, int year, CancellationToken cancellationToken)
    {
        var pendingUserIds = await _repo.GetPendingRequesterUserIdsForSeasonAsync(seasonId, cancellationToken);
        if (pendingUserIds.Count == 0)
        {
            return;
        }

        // The lead meter filters by season status (Active/Full), so closing a
        // season drops that camp's pending requests from the count — refresh
        // the cached badges.
        await InvalidateLeadBadgesAsync(campId, cancellationToken);

        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        var campName = camp?.Seasons.FirstOrDefault(s => s.Id == seasonId)?.Name ?? camp?.Slug ?? "a camp";
        var slug = camp?.Slug;

        try
        {
            await _notificationEmitter.SendAsync(
                NotificationSource.CampMembershipSeasonClosed,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"The {year} season for {campName} is no longer open",
                pendingUserIds,
                body: "Your pending request to join this camp won't be reviewed because the season was withdrawn or rejected.",
                actionUrl: slug is null ? null : $"/Barrios/{slug}",
                actionLabel: slug is null ? null : "View camp",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send CampMembershipSeasonClosed notification for season {SeasonId}", seasonId);
        }
    }

    public async Task SetSeasonFullAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status != CampSeasonStatus.Active)
            {
                throw new InvalidOperationException($"Cannot set full on a season with status {season.Status}.");
            }

            season.Status = CampSeasonStatus.Full;
            season.UpdatedAt = now;

            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (!found)
        {
            throw new InvalidOperationException("Season not found.");
        }

        await _auditLog.LogAsync(
            AuditAction.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {year} marked as full",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        InvalidateCache(year);
    }

    public async Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;
        var previousStatus = CampSeasonStatus.Pending;
        var newStatus = CampSeasonStatus.Pending;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status != CampSeasonStatus.Full && season.Status != CampSeasonStatus.Withdrawn)
            {
                throw new InvalidOperationException($"Cannot reactivate a season with status {season.Status}.");
            }

            // Withdrawn camps go back to Pending for re-approval; Full camps go back to Active
            previousStatus = season.Status;
            newStatus = season.Status == CampSeasonStatus.Withdrawn
                ? CampSeasonStatus.Pending
                : CampSeasonStatus.Active;
            season.Status = newStatus;
            season.UpdatedAt = now;

            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (!found)
        {
            throw new InvalidOperationException("Season not found.");
        }

        await _auditLog.LogAsync(
            AuditAction.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {year} status changed from {previousStatus} to {newStatus}",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        InvalidateCache(year);
    }

    // ==========================================================================
    // Camp updates
    // ==========================================================================

    public async Task UpdateCampAsync(
        Guid campId, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        bool hideHistoricalNames,
        CancellationToken cancellationToken = default)
    {
        var updated = await _repo.UpdateCampFieldsAsync(
            campId,
            contactEmail,
            contactPhone,
            webOrSocialUrl,
            links,
            isSwissCamp,
            timesAtNowhere,
            hideHistoricalNames,
            _clock.GetCurrentInstant(),
            cancellationToken);

        if (!updated)
        {
            throw new InvalidOperationException("Camp not found.");
        }

        await _auditLog.LogAsync(
            AuditAction.CampUpdated, nameof(Camp), campId,
            $"Updated camp {campId}",
            "CampService");

        await InvalidateCampYearCachesAsync(campId, cancellationToken);
    }

    public async Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        var campYears = await _repo.GetCampYearsAsync(campId, cancellationToken);

        var deletedImagePaths = await _repo.DeleteCampAsync(campId, cancellationToken);
        if (deletedImagePaths is null)
        {
            throw new InvalidOperationException("Camp not found.");
        }

        _imageStorage.DeleteImages(deletedImagePaths);

        await _auditLog.LogAsync(
            AuditAction.CampDeleted, nameof(Camp), campId,
            $"Camp {campId} permanently deleted",
            "CampService");

        InvalidateCampYearCaches(campYears);
    }

    // ==========================================================================
    // Lead management
    // ==========================================================================

    public async Task<CampLead> AddLeadAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (await _repo.IsUserActiveLeadAsync(userId, campId, cancellationToken))
        {
            throw new InvalidOperationException("This user is already an active lead.");
        }

        var activeCount = await _repo.CountActiveLeadsAsync(campId, cancellationToken);
        if (activeCount >= 5)
        {
            throw new InvalidOperationException("Camp already has the maximum of 5 leads.");
        }

        var now = _clock.GetCurrentInstant();
        var lead = new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            UserId = userId,
            Role = CampLeadRole.CoLead,
            JoinedAt = now
        };

        await _repo.AddLeadAsync(lead, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampLeadAdded, nameof(CampLead), lead.Id,
            "Added as camp lead",
            userId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await _systemTeamSync.SyncBarrioLeadsMembershipForUserAsync(userId, cancellationToken);

        return lead;
    }

    public async Task RemoveLeadAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        var lead = await _repo.GetLeadForMutationAsync(leadId, cancellationToken)
            ?? throw new InvalidOperationException("Lead not found.");

        var activeCount = await _repo.CountActiveLeadsAsync(lead.CampId, cancellationToken);
        if (activeCount <= 1)
        {
            throw new InvalidOperationException(
                "Cannot remove the last lead. A camp must have at least one lead.");
        }

        lead.LeftAt = _clock.GetCurrentInstant();
        await _repo.UpdateLeadAsync(lead, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampLeadRemoved, nameof(CampLead), leadId,
            "Removed from camp leads",
            lead.UserId,
            relatedEntityId: lead.CampId, relatedEntityType: nameof(Camp));

        await _systemTeamSync.SyncBarrioLeadsMembershipForUserAsync(lead.UserId, cancellationToken);
    }

    // ==========================================================================
    // Historical names
    // ==========================================================================

    public async Task AddHistoricalNameAsync(
        Guid campId, string name, CancellationToken cancellationToken = default)
    {
        var entry = new CampHistoricalName
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Name = name.Trim(),
            Source = CampNameSource.Manual,
            CreatedAt = _clock.GetCurrentInstant()
        };

        await _repo.AddHistoricalNameAsync(entry, cancellationToken);
    }

    public async Task RemoveHistoricalNameAsync(
        Guid historicalNameId, CancellationToken cancellationToken = default)
    {
        var removed = await _repo.RemoveHistoricalNameAsync(historicalNameId, cancellationToken);
        if (!removed)
        {
            throw new InvalidOperationException("Historical name not found.");
        }
    }

    // ==========================================================================
    // Cross-service queries (used by CityPlanningService)
    // ==========================================================================

    public Task<SoundZone?> GetCampSeasonSoundZoneAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        _repo.GetSeasonSoundZoneAsync(campSeasonId, cancellationToken);

    public Task<string?> GetCampSeasonNameAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        _repo.GetSeasonNameAsync(campSeasonId, cancellationToken);

    public async Task<CampSeasonInfo?> GetCampSeasonInfoAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var info = await _repo.GetSeasonInfoAsync(campSeasonId, cancellationToken);
        return info is null
            ? null
            : new CampSeasonInfo(info.Value.CampSeasonId, info.Value.CampId, info.Value.Year);
    }

    public async Task<IReadOnlyDictionary<Guid, CampSeasonDisplayData>> GetCampSeasonDisplayDataForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var rows = await _repo.GetSeasonDisplayDataForYearAsync(year, cancellationToken);
        return rows.ToDictionary(
            kv => kv.Key,
            kv => new CampSeasonDisplayData(
                kv.Value.Name,
                kv.Value.CampSlug,
                kv.Value.SoundZone,
                kv.Value.SpaceRequirement));
    }

    public async Task<IReadOnlyList<CampSeasonBrief>> GetCampSeasonBriefsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var rows = await _repo.GetSeasonBriefsForYearAsync(year, cancellationToken);
        return rows
            .Select(r => new CampSeasonBrief(r.Id, r.Name, r.CampSlug, r.SpaceRequirement))
            .ToList();
    }

    public Task<Guid?> GetCampLeadSeasonIdForYearAsync(
        Guid userId, int year, CancellationToken cancellationToken = default) =>
        _repo.GetCampLeadSeasonIdForYearAsync(userId, year, cancellationToken);

    // ==========================================================================
    // Authorization checks
    // ==========================================================================

    public Task<bool> IsUserCampLeadAsync(
        Guid userId, Guid campId, CancellationToken cancellationToken = default) =>
        _repo.IsUserActiveLeadAsync(userId, campId, cancellationToken);

    // ==========================================================================
    // Images
    // ==========================================================================

    public async Task<CampImage> UploadImageAsync(
        Guid campId, Stream fileStream, string fileName, string contentType, long length,
        CancellationToken cancellationToken = default)
    {
        var imageCount = await _repo.CountImagesAsync(campId, cancellationToken);
        if (imageCount >= 5)
        {
            throw new InvalidOperationException("Maximum 5 images per camp.");
        }

        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/png", "image/webp"
        };
        if (!allowedTypes.Contains(contentType))
        {
            throw new InvalidOperationException("Only JPEG, PNG, and WebP images are allowed.");
        }

        if (length > 10 * 1024 * 1024)
        {
            throw new InvalidOperationException("Image must be under 10MB.");
        }

        var relativePath = await _imageStorage.SaveImageAsync(
            campId, fileStream, contentType, cancellationToken);

        var image = new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            FileName = fileName,
            StoragePath = relativePath,
            ContentType = contentType,
            SortOrder = imageCount,
            UploadedAt = _clock.GetCurrentInstant()
        };

        await _repo.AddImageAsync(image, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampImageUploaded, nameof(CampImage), image.Id,
            $"Uploaded image '{fileName}'",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await InvalidateCampYearCachesAsync(campId, cancellationToken);

        return image;
    }

    public async Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        var result = await _repo.DeleteImageAsync(imageId, cancellationToken)
            ?? throw new InvalidOperationException("Image not found.");

        _imageStorage.DeleteImage(result.StoragePath);

        await _auditLog.LogAsync(
            AuditAction.CampImageDeleted, nameof(CampImage), imageId,
            $"Deleted image {imageId}",
            "CampService",
            relatedEntityId: result.CampId, relatedEntityType: nameof(Camp));

        await InvalidateCampYearCachesAsync(result.CampId, cancellationToken);
    }

    public async Task ReorderImagesAsync(
        Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default)
    {
        await _repo.ReorderImagesAsync(campId, imageIdsInOrder, cancellationToken);
        await InvalidateCampYearCachesAsync(campId, cancellationToken);
    }

    // ==========================================================================
    // Settings (CampAdmin)
    // ==========================================================================

    public async Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default)
    {
        await _repo.SetPublicYearAsync(year, cancellationToken);
        _cache.InvalidateCampSettings();
    }

    public async Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        var changed = await _repo.OpenSeasonAsync(year, cancellationToken);
        if (changed)
        {
            _cache.InvalidateCampSettings();
        }
    }

    public async Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        var changed = await _repo.CloseSeasonAsync(year, cancellationToken);
        if (changed)
        {
            _cache.InvalidateCampSettings();
        }
    }

    public async Task SetNameLockDateAsync(
        int year, LocalDate lockDate, CancellationToken cancellationToken = default)
    {
        await _repo.SetNameLockDateForYearAsync(year, lockDate, cancellationToken);
        InvalidateCache(year);
    }

    public async Task<Dictionary<int, LocalDate?>> GetNameLockDatesAsync(
        List<int> years, CancellationToken cancellationToken = default)
    {
        var result = await _repo.GetNameLockDatesAsync(years, cancellationToken);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // ==========================================================================
    // Name change
    // ==========================================================================

    public async Task ChangeSeasonNameAsync(
        Guid seasonId, string newName, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        string? oldName = null;
        var campId = Guid.Empty;
        var year = 0;

        var found = await _repo.ApplyNameChangeAsync(seasonId, season =>
        {
            if (season.NameLockDate.HasValue && today >= season.NameLockDate.Value)
            {
                throw new InvalidOperationException("Season name is locked and cannot be changed.");
            }

            if (string.Equals(season.Name, newName, StringComparison.Ordinal))
            {
                return null;
            }

            oldName = season.Name;
            campId = season.CampId;
            year = season.Year;

            var historyEntry = new CampHistoricalName
            {
                Id = Guid.NewGuid(),
                CampId = season.CampId,
                Name = season.Name,
                Year = season.Year,
                Source = CampNameSource.NameChange,
                CreatedAt = now
            };

            season.Name = newName;
            season.UpdatedAt = now;

            return historyEntry;
        }, cancellationToken);

        if (!found)
        {
            throw new InvalidOperationException("Season not found.");
        }

        if (oldName is null)
        {
            // No-op: same name.
            return;
        }

        await _auditLog.LogAsync(
            AuditAction.CampNameChanged, nameof(CampSeason), seasonId,
            $"Name changed from '{oldName}' to '{newName}'",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        InvalidateCache(year);
    }

    // ==========================================================================
    // Private helpers
    // ==========================================================================

    private void InvalidateCache(int year)
    {
        _cache.InvalidateCampSeasonsByYear(year);
    }

    private async Task InvalidateCampYearCachesAsync(Guid campId, CancellationToken cancellationToken)
    {
        var years = await _repo.GetCampYearsAsync(campId, cancellationToken);
        InvalidateCampYearCaches(years);
    }

    private void InvalidateCampYearCaches(IEnumerable<int> years)
    {
        foreach (var year in years)
        {
            InvalidateCache(year);
        }
    }

    private static CampSeason CreateSeasonFromData(
        Guid campId, int year, string name, CampSeasonData data, Instant now)
    {
        return new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Year = year,
            Name = name,
            Status = CampSeasonStatus.Pending,
            BlurbLong = data.BlurbLong,
            BlurbShort = data.BlurbShort,
            Languages = data.Languages,
            AcceptingMembers = data.AcceptingMembers,
            KidsWelcome = data.KidsWelcome,
            KidsVisiting = data.KidsVisiting,
            KidsAreaDescription = data.KidsAreaDescription,
            HasPerformanceSpace = data.HasPerformanceSpace,
            PerformanceTypes = data.PerformanceTypes,
            Vibes = new List<CampVibe>(data.Vibes),
            AdultPlayspace = data.AdultPlayspace,
            MemberCount = data.MemberCount,
            SpaceRequirement = data.SpaceRequirement,
            SoundZone = data.SoundZone,
            ContainerCount = data.ContainerCount,
            ContainerNotes = data.ContainerNotes,
            ElectricalGrid = data.ElectricalGrid,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    // ==========================================================================
    // Camp membership per season (issue nobodies-collective#488)
    // ==========================================================================

    private async Task<CampSeason?> ResolveOpenMembershipSeasonAsync(
        Guid campId, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        return camp?.Seasons.FirstOrDefault(s =>
            s.Year == settings.PublicYear
            && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full));
    }

    private async Task InvalidateLeadBadgesAsync(Guid campId, CancellationToken cancellationToken)
    {
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null)
        {
            return;
        }
        foreach (var leadUserId in camp.Leads.Where(l => l.LeftAt is null).Select(l => l.UserId).Distinct())
        {
            _leadBadgeInvalidator.Invalidate(leadUserId);
        }
    }

    public async Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var season = await ResolveOpenMembershipSeasonAsync(campId, cancellationToken);
        if (season is null)
        {
            return new CampMemberRequestResult(
                Guid.Empty,
                CampMemberRequestOutcome.NoOpenSeason,
                "Camp is not open for membership this year.");
        }

        var now = _clock.GetCurrentInstant();
        var insert = await _repo.RequestMembershipAsync(season.Id, userId, now, cancellationToken);

        if (insert.Outcome == CampMemberInsertOutcome.Created)
        {
            await _auditLog.LogAsync(
                AuditAction.CampMemberRequested, nameof(CampMember), insert.MemberId,
                $"Requested membership in camp season {season.Year}",
                userId,
                relatedEntityId: campId, relatedEntityType: nameof(Camp));
            await InvalidateLeadBadgesAsync(campId, cancellationToken);
        }

        return insert.Outcome switch
        {
            CampMemberInsertOutcome.Created =>
                new CampMemberRequestResult(insert.MemberId, CampMemberRequestOutcome.Created),
            CampMemberInsertOutcome.AlreadyActive =>
                new CampMemberRequestResult(insert.MemberId, CampMemberRequestOutcome.AlreadyActive),
            _ =>
                new CampMemberRequestResult(insert.MemberId, CampMemberRequestOutcome.AlreadyPending)
        };
    }

    public async Task ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot approve a camp member with status {member.Status}.");
        }

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Active;
        member.ConfirmedAt = now;
        member.ConfirmedByUserId = approvedByUserId;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampMemberApproved, nameof(CampMember), member.Id,
            $"Approved camp membership for season {member.CampSeason.Year}",
            approvedByUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));

        await InvalidateLeadBadgesAsync(scopedCampId, cancellationToken);

        var camp = await _repo.GetByIdAsync(scopedCampId, cancellationToken);
        var campName = camp?.Seasons.FirstOrDefault(s => s.Id == member.CampSeasonId)?.Name ?? camp?.Slug ?? "a camp";
        var slug = camp?.Slug;
        try
        {
            await _notificationEmitter.SendAsync(
                NotificationSource.CampMembershipApproved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Your request to join {campName} was approved",
                [member.UserId],
                actionUrl: slug is null ? null : $"/Barrios/{slug}",
                actionLabel: slug is null ? null : "View camp",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify requester {UserId} about approved camp membership {MemberId}", member.UserId, member.Id);
        }
    }

    public async Task RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot reject a camp member with status {member.Status}.");
        }

        var requesterUserId = member.UserId;
        var seasonId = member.CampSeasonId;

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = rejectedByUserId;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampMemberRejected, nameof(CampMember), member.Id,
            $"Rejected camp membership request for season {member.CampSeason.Year}",
            rejectedByUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));

        await InvalidateLeadBadgesAsync(scopedCampId, cancellationToken);

        var camp = await _repo.GetByIdAsync(scopedCampId, cancellationToken);
        var campName = camp?.Seasons.FirstOrDefault(s => s.Id == seasonId)?.Name ?? camp?.Slug ?? "a camp";
        try
        {
            await _notificationEmitter.SendAsync(
                NotificationSource.CampMembershipRejected,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Your request to join {campName} was not approved",
                [requesterUserId],
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify requester {UserId} about rejected camp membership {MemberId}", requesterUserId, member.Id);
        }
    }

    public async Task RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Active)
        {
            throw new InvalidOperationException($"Cannot remove a camp member with status {member.Status}.");
        }

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = removedByUserId;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampMemberRemoved, nameof(CampMember), member.Id,
            $"Removed camp member from season {member.CampSeason.Year}",
            removedByUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));
    }

    public async Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot withdraw a camp member request with status {member.Status}.");
        }

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = userId;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampMemberWithdrawn, nameof(CampMember), member.Id,
            $"Withdrew camp membership request for season {member.CampSeason.Year}",
            userId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        await InvalidateLeadBadgesAsync(member.CampSeason.CampId, cancellationToken);
    }

    public async Task LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Active)
        {
            throw new InvalidOperationException($"Cannot leave a camp membership with status {member.Status}.");
        }

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = userId;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampMemberLeft, nameof(CampMember), member.Id,
            $"Left camp season {member.CampSeason.Year}",
            userId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));
    }

    public async Task<CampMembershipState> GetMembershipStateForCampAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var season = await ResolveOpenMembershipSeasonAsync(campId, cancellationToken);
        if (season is null)
        {
            return new CampMembershipState(null, null, null, CampMemberStatusSummary.NoOpenSeason);
        }

        var member = await _repo.GetUserMembershipInSeasonAsync(season.Id, userId, cancellationToken);
        if (member is null)
        {
            return new CampMembershipState(season.Year, season.Id, null, CampMemberStatusSummary.None);
        }

        var summary = member.Status == CampMemberStatus.Active
            ? CampMemberStatusSummary.Active
            : CampMemberStatusSummary.Pending;
        return new CampMembershipState(season.Year, season.Id, member.Id, summary);
    }

    public async Task<CampMemberListData> GetCampMembersAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var infoOpt = await _repo.GetSeasonInfoAsync(campSeasonId, cancellationToken);
        if (infoOpt is null)
        {
            throw new InvalidOperationException("Season not found.");
        }
        var info = infoOpt.Value;

        var members = await _repo.GetSeasonMembersAsync(campSeasonId, cancellationToken);

        // Fold active leads of the camp into the active list so the roster shows
        // the people running the camp even if they never requested membership.
        // Leads also on a CampMember row get IsLead=true stamped; leads without a
        // row appear as synthetic entries with CampMemberId = Guid.Empty.
        var camp = await _repo.GetByIdAsync(info.CampId, cancellationToken);
        var activeLeads = camp?.Leads.Where(l => l.LeftAt is null).ToList() ?? new List<CampLead>();
        var leadUserIds = activeLeads.Select(l => l.UserId).ToHashSet();

        var userIds = members.Select(m => m.UserId)
            .Concat(activeLeads.Select(l => l.UserId))
            .Distinct()
            .ToList();
        var users = await _userService.GetByIdsAsync(userIds, cancellationToken);
        var userMap = users.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.DisplayName);

        static string DisplayName(Guid userId, IReadOnlyDictionary<Guid, string> names) =>
            names.GetValueOrDefault(userId) ?? "Unknown";

        var pending = members
            .Where(m => m.Status == CampMemberStatus.Pending)
            .Select(m => new CampMemberRow(
                m.Id, m.UserId, DisplayName(m.UserId, userMap), m.RequestedAt, m.ConfirmedAt,
                IsLead: leadUserIds.Contains(m.UserId)))
            .ToList();

        var activeMemberUserIds = members
            .Where(m => m.Status == CampMemberStatus.Active)
            .Select(m => m.UserId)
            .ToHashSet();

        var activeFromMembers = members
            .Where(m => m.Status == CampMemberStatus.Active)
            .Select(m => new CampMemberRow(
                m.Id, m.UserId, DisplayName(m.UserId, userMap), m.RequestedAt, m.ConfirmedAt,
                IsLead: leadUserIds.Contains(m.UserId)));

        var activeFromLeads = activeLeads
            .Where(l => !activeMemberUserIds.Contains(l.UserId))
            .Select(l => new CampMemberRow(
                CampMemberId: Guid.Empty,
                UserId: l.UserId,
                DisplayName: DisplayName(l.UserId, userMap),
                RequestedAt: l.JoinedAt,
                ConfirmedAt: l.JoinedAt,
                IsLead: true));

        var active = activeFromMembers
            .Concat(activeFromLeads)
            .OrderByDescending(r => r.IsLead)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CampMemberListData(campSeasonId, info.Year, pending, active);
    }

    public Task<int> GetPendingMembershipCountForLeadAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _repo.CountPendingMembershipsForLeadAsync(userId, cancellationToken);

    public async Task<IReadOnlyList<CampMembershipSummary>> GetCampMembershipsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var members = await _repo.GetUserMembershipsAsync(userId, cancellationToken);
        return members
            .Select(m => new CampMembershipSummary(
                m.Id,
                m.CampSeason.CampId,
                m.CampSeason.Camp.Slug,
                m.CampSeason.Name,
                m.CampSeasonId,
                m.CampSeason.Year,
                m.Status,
                m.RequestedAt,
                m.ConfirmedAt))
            .ToList();
    }

    // ==========================================================================
    // IUserDataContributor — GDPR export
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var leadAssignments = await _repo.GetAllLeadAssignmentsForUserAsync(userId, ct);

        var shaped = leadAssignments.Select(cl => new
        {
            CampSlug = cl.Camp.Slug,
            cl.Role,
            JoinedAt = cl.JoinedAt.ToInvariantInstantString(),
            LeftAt = cl.LeftAt.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.CampLeadAssignments, shaped)];
    }
}
