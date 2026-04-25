using Humans.Application.Interfaces;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CampService : ICampService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CampsForYearCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CampSettingsCacheTtl = TimeSpan.FromMinutes(5);

    public CampService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        ISystemTeamSync systemTeamSync,
        IClock clock,
        IMemoryCache cache)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _systemTeamSync = systemTeamSync;
        _clock = clock;
        _cache = cache;
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
            throw new InvalidOperationException($"The name '{name}' generates a reserved slug.");

        // Ensure unique slug
        var baseSlug = slug;
        var suffix = 2;
        while (await _dbContext.Camps.AnyAsync(b => b.Slug == slug, cancellationToken))
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

        _dbContext.Camps.Add(camp);

        // TODO: Check settings for a default NameLockDate for new registrations
        var season = CreateSeasonFromData(camp.Id, year, name, seasonData, now);
        _dbContext.CampSeasons.Add(season);

        var lead = new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            UserId = createdByUserId,
            Role = CampLeadRole.CoLead,
            JoinedAt = now
        };

        _dbContext.CampLeads.Add(lead);

        if (historicalNames is { Count: > 0 })
        {
            foreach (var oldName in historicalNames)
            {
                _dbContext.CampHistoricalNames.Add(new CampHistoricalName
                {
                    Id = Guid.NewGuid(),
                    CampId = camp.Id,
                    Name = oldName,
                    Source = CampNameSource.Manual,
                    CreatedAt = now
                });
            }
        }

        await _auditLogService.LogAsync(
            AuditAction.CampCreated, nameof(Camp), camp.Id,
            $"Registered camp '{name}' for {year}",
            createdByUserId);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _systemTeamSync.SyncBarrioLeadsMembershipForUserAsync(createdByUserId, cancellationToken);
        InvalidateCache(year);

        return camp;
    }

    // ==========================================================================
    // Queries
    // ==========================================================================

    public async Task<Camp?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalizedSlug = slug.ToLowerInvariant();

        return await _dbContext.Camps
            .Include(b => b.Seasons)
            .Include(b => b.Leads.Where(l => l.LeftAt == null))
                .ThenInclude(l => l.User)
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(b => b.Slug == normalizedSlug, cancellationToken);
    }

    public async Task<Camp?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Camps
            .Include(b => b.Seasons)
            .Include(b => b.Leads.Where(l => l.LeftAt == null))
                .ThenInclude(l => l.User)
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(b => b.Id == campId, cancellationToken);
    }

    public async Task<CampDetailData?> GetCampDetailAsync(
        string slug,
        int? preferredYear = null,
        bool fallbackToLatestSeason = true,
        CancellationToken cancellationToken = default)
    {
        var camp = await GetCampBySlugAsync(slug, cancellationToken);
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
            camp.Leads
                .Where(l => l.IsActive)
                .Select(l => new CampLeadSummary(l.Id, l.UserId, l.User.DisplayName))
                .ToList(),
            CreateCampSeasonDetailData(season));
    }

    public async Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        int? preferredYear = null,
        CancellationToken cancellationToken = default)
    {
        var camp = await GetCampByIdAsync(campId, cancellationToken);
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

        return season is null
            ? null
            : CreateCampEditData(camp, season);
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
            var leadCamps = await GetCampsByLeadUserIdAsync(userId.Value, cancellationToken);
            myCamps = leadCamps
                .Where(camp => camp.Seasons.Any(season =>
                    season.Year == year &&
                    season.Status != CampSeasonStatus.Active &&
                    season.Status != CampSeasonStatus.Full))
                .Where(camp => cards.All(card => card.Id != camp.Id))
                .Select(camp => CreateCampDirectoryCard(camp, year))
                .ToList();
        }

        var pendingCount = await _dbContext.CampSeasons
            .CountAsync(season => season.Status == CampSeasonStatus.Pending, cancellationToken);

        return new CampDirectoryResult(year, pendingCount, cards, myCamps);
    }

    public async Task<List<Camp>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrCreateAsync(CacheKeys.CampSeasonsByYear(year), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CampsForYearCacheTtl;
            return await _dbContext.Camps
                .Include(b => b.Seasons.Where(s => s.Year == year &&
                    (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full)))
                .Include(b => b.Images.OrderBy(i => i.SortOrder))
                .Include(b => b.HistoricalNames)
                .Where(b => b.Seasons.Any(s => s.Year == year &&
                    (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full)))
                .ToListAsync(cancellationToken);
        }) ?? [];
    }

    public async Task<List<Camp>> GetAllCampsForYearAsync(int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Camps
            .Include(b => b.Seasons.Where(s => s.Year == year))
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .Include(b => b.HistoricalNames)
            .Where(b => b.Seasons.Any(s => s.Year == year))
            .ToListAsync(cancellationToken);
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
    public async Task<List<Camp>> GetCampsWithLeadsForYearAsync(int year, IReadOnlyList<CampSeasonStatus>? statusFilter = null, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Camps
            .Include(c => c.Seasons.Where(s => s.Year == year))
            .Include(c => c.Leads.Where(l => l.LeftAt == null))
                .ThenInclude(l => l.User)
            .Where(c => c.Seasons.Any(s => s.Year == year));

        if (statusFilter is { Count: > 0 })
        {
            query = query.Where(c => c.Seasons.Any(s => s.Year == year && statusFilter.Contains(s.Status)));
        }

        return await query
            .OrderBy(c => c.Seasons.Where(s => s.Year == year).Select(s => s.Name).FirstOrDefault())
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Camp>> GetCampsByLeadUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Camps
            .Include(b => b.Seasons)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .Include(b => b.Leads)
            .Where(b => b.Leads.Any(l => l.UserId == userId && l.LeftAt == null))
            .ToListAsync(cancellationToken);
    }

    public async Task<CampSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrCreateAsync(CacheKeys.CampSettings, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CampSettingsCacheTtl;
            return await _dbContext.CampSettings
                .AsNoTracking()
                .OrderBy(s => s.Id)
                .FirstAsync(cancellationToken);
        }) ?? throw new InvalidOperationException("Camp settings not found.");
    }

    public async Task<List<CampSeason>> GetPendingSeasonsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Include(s => s.Camp)
                .ThenInclude(b => b.Leads.Where(l => l.LeftAt == null))
                    .ThenInclude(l => l.User)
            .Where(s => s.Status == CampSeasonStatus.Pending)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
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

    private IReadOnlyList<CampLink> CreateCampLinks(Camp camp)
    {
        if (camp.Links is { Count: > 0 })
        {
            return camp.Links;
        }

        return camp.WebOrSocialUrl is not null
            ? [new CampLink { Url = camp.WebOrSocialUrl }]
            : [];
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

    private CampEditData CreateCampEditData(Camp camp, CampSeason season)
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
            camp.Leads
                .Where(l => l.IsActive)
                .Select(l => new CampLeadSummary(l.Id, l.UserId, l.User.DisplayName))
                .ToList(),
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

    public async Task<CampSeason> OptInToSeasonAsync(Guid campId, int year,
        CancellationToken cancellationToken = default)
    {
        // Verify season is open
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.OpenSeasons.Contains(year))
            throw new InvalidOperationException($"Season {year} is not open for registration.");

        // Check no existing season for this year
        var existing = await _dbContext.CampSeasons
            .AnyAsync(s => s.CampId == campId && s.Year == year, cancellationToken);
        if (existing)
            throw new InvalidOperationException($"Camp already has a season for {year}.");

        // Copy from most recent season
        var previousSeason = await _dbContext.CampSeasons
            .Where(s => s.CampId == campId)
            .OrderByDescending(s => s.Year)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No previous season to copy from.");

        // Auto-approve only if a prior season reached an approved status
        var approvedStatuses = new[]
        {
            CampSeasonStatus.Active, CampSeasonStatus.Full,
            CampSeasonStatus.Withdrawn
        };
        var hasApprovedSeason = await _dbContext.CampSeasons
            .AnyAsync(s => s.CampId == campId && approvedStatuses.Contains(s.Status),
                cancellationToken);

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

        _dbContext.CampSeasons.Add(newSeason);

        await _auditLogService.LogAsync(
            AuditAction.CampSeasonCreated, nameof(CampSeason), newSeason.Id,
            $"Opted in to season {year} (auto-approved: {hasApprovedSeason})",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(year);

        return newSeason;
    }

    public async Task UpdateSeasonAsync(Guid seasonId, CampSeasonData data,
        CancellationToken cancellationToken = default)
    {
        var season = await _dbContext.CampSeasons.FindAsync([seasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");

        var now = _clock.GetCurrentInstant();
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

        await _auditLogService.LogAsync(
            AuditAction.CampUpdated, nameof(CampSeason), seasonId,
            $"Updated season {season.Year} details",
            "CampService",
            relatedEntityId: season.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(season.Year);
    }

    public async Task ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes,
        CancellationToken cancellationToken = default)
    {
        var season = await _dbContext.CampSeasons.FindAsync([seasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");
        if (season.Status != CampSeasonStatus.Pending)
            throw new InvalidOperationException($"Cannot approve a season with status {season.Status}.");

        var now = _clock.GetCurrentInstant();
        season.Status = CampSeasonStatus.Active;
        season.ReviewedByUserId = reviewedByUserId;
        season.ReviewNotes = notes;
        season.ResolvedAt = now;
        season.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.CampSeasonApproved, nameof(CampSeason), seasonId,
            $"Approved season {season.Year}",
            reviewedByUserId,
            relatedEntityId: season.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(season.Year);
    }

    public async Task RejectSeasonAsync(Guid seasonId, Guid reviewedByUserId, string notes,
        CancellationToken cancellationToken = default)
    {
        var season = await _dbContext.CampSeasons.FindAsync([seasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");
        if (season.Status != CampSeasonStatus.Pending)
            throw new InvalidOperationException($"Cannot reject a season with status {season.Status}.");

        var now = _clock.GetCurrentInstant();
        season.Status = CampSeasonStatus.Rejected;
        season.ReviewedByUserId = reviewedByUserId;
        season.ReviewNotes = notes;
        season.ResolvedAt = now;
        season.UpdatedAt = now;

        await AutoWithdrawPendingMembershipsForSeasonAsync(seasonId, now, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.CampSeasonRejected, nameof(CampSeason), seasonId,
            $"Rejected season {season.Year}: {notes}",
            reviewedByUserId,
            relatedEntityId: season.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(season.Year);
    }

    public async Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var season = await _dbContext.CampSeasons.FindAsync([seasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");
        if (season.Status != CampSeasonStatus.Pending && season.Status != CampSeasonStatus.Active)
            throw new InvalidOperationException($"Cannot withdraw a season with status {season.Status}.");

        var now = _clock.GetCurrentInstant();
        season.Status = CampSeasonStatus.Withdrawn;
        season.UpdatedAt = now;

        await AutoWithdrawPendingMembershipsForSeasonAsync(seasonId, now, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.CampSeasonWithdrawn, nameof(CampSeason), seasonId,
            $"Withdrew from season {season.Year}",
            "CampService",
            relatedEntityId: season.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(season.Year);
    }

    private async Task AutoWithdrawPendingMembershipsForSeasonAsync(
        Guid seasonId, Instant now, CancellationToken cancellationToken)
    {
        var pending = await _dbContext.CampMembers
            .Where(m => m.CampSeasonId == seasonId && m.Status == CampMemberStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var member in pending)
        {
            member.Status = CampMemberStatus.Removed;
            member.RemovedAt = now;
        }
    }

    public async Task SetSeasonFullAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var season = await _dbContext.CampSeasons.FindAsync([seasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");
        if (season.Status != CampSeasonStatus.Active)
            throw new InvalidOperationException($"Cannot set full on a season with status {season.Status}.");

        var now = _clock.GetCurrentInstant();
        season.Status = CampSeasonStatus.Full;
        season.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {season.Year} marked as full",
            "CampService",
            relatedEntityId: season.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(season.Year);
    }

    public async Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var season = await _dbContext.CampSeasons.FindAsync([seasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");
        if (season.Status != CampSeasonStatus.Full && season.Status != CampSeasonStatus.Withdrawn)
            throw new InvalidOperationException($"Cannot reactivate a season with status {season.Status}.");

        var now = _clock.GetCurrentInstant();
        // Withdrawn camps go back to Pending for re-approval; Full camps go back to Active
        var previousStatus = season.Status;
        season.Status = season.Status == CampSeasonStatus.Withdrawn
            ? CampSeasonStatus.Pending
            : CampSeasonStatus.Active;
        season.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {season.Year} status changed from {previousStatus} to {season.Status}",
            "CampService",
            relatedEntityId: season.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(season.Year);
    }

    // ==========================================================================
    // Camp updates
    // ==========================================================================

    public async Task UpdateCampAsync(Guid campId, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        bool hideHistoricalNames,
        CancellationToken cancellationToken = default)
    {
        var camp = await _dbContext.Camps.FindAsync([campId], cancellationToken)
            ?? throw new InvalidOperationException("Camp not found.");

        camp.ContactEmail = contactEmail;
        camp.ContactPhone = contactPhone;
        camp.WebOrSocialUrl = webOrSocialUrl;
        camp.Links = links;
        if (links is { Count: > 0 })
            camp.WebOrSocialUrl = null;
        camp.IsSwissCamp = isSwissCamp;
        camp.HideHistoricalNames = hideHistoricalNames;
        camp.TimesAtNowhere = timesAtNowhere;
        camp.UpdatedAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.CampUpdated, nameof(Camp), campId,
            $"Updated camp '{camp.Slug}'",
            "CampService");

        await _dbContext.SaveChangesAsync(cancellationToken);
        await InvalidateCampYearCachesAsync(campId, cancellationToken);
    }

    public async Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        var camp = await _dbContext.Camps.FindAsync([campId], cancellationToken)
            ?? throw new InvalidOperationException("Camp not found.");
        var campYears = await GetCampYearsAsync(campId, cancellationToken);

        // Delete images from filesystem
        var images = await _dbContext.CampImages
            .Where(i => i.CampId == campId).ToListAsync(cancellationToken);
        foreach (var img in images)
        {
            var fullPath = Path.Combine("wwwroot", img.StoragePath);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }

        await _auditLogService.LogAsync(
            AuditAction.CampDeleted, nameof(Camp), campId,
            $"Camp '{camp.Slug}' permanently deleted",
            "CampService");

        _dbContext.Camps.Remove(camp); // cascade deletes children
        await _dbContext.SaveChangesAsync(cancellationToken);

        InvalidateCampYearCaches(campYears);
    }

    // ==========================================================================
    // Lead management
    // ==========================================================================

    public async Task<CampLead> AddLeadAsync(Guid campId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        var alreadyLead = await _dbContext.CampLeads
            .AnyAsync(l => l.CampId == campId && l.UserId == userId && l.LeftAt == null, cancellationToken);
        if (alreadyLead)
            throw new InvalidOperationException("This user is already an active lead.");

        var activeCount = await _dbContext.CampLeads
            .CountAsync(l => l.CampId == campId && l.LeftAt == null, cancellationToken);
        if (activeCount >= 5)
            throw new InvalidOperationException("Camp already has the maximum of 5 leads.");

        var now = _clock.GetCurrentInstant();
        var lead = new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            UserId = userId,
            Role = CampLeadRole.CoLead,
            JoinedAt = now
        };

        _dbContext.CampLeads.Add(lead);

        await _auditLogService.LogAsync(
            AuditAction.CampLeadAdded, nameof(CampLead), lead.Id,
            "Added as camp lead",
            userId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _systemTeamSync.SyncBarrioLeadsMembershipForUserAsync(userId, cancellationToken);

        return lead;
    }

    public async Task RemoveLeadAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        var lead = await _dbContext.CampLeads.FindAsync([leadId], cancellationToken)
            ?? throw new InvalidOperationException("Lead not found.");

        var activeCount = await _dbContext.CampLeads
            .CountAsync(l => l.CampId == lead.CampId && l.LeftAt == null, cancellationToken);
        if (activeCount <= 1)
            throw new InvalidOperationException("Cannot remove the last lead. A camp must have at least one lead.");

        lead.LeftAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.CampLeadRemoved, nameof(CampLead), leadId,
            "Removed from camp leads",
            lead.UserId,
            relatedEntityId: lead.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _systemTeamSync.SyncBarrioLeadsMembershipForUserAsync(lead.UserId, cancellationToken);
    }


    // ==========================================================================
    // Historical names
    // ==========================================================================

    public async Task AddHistoricalNameAsync(Guid campId, string name,
        CancellationToken cancellationToken = default)
    {
        var entry = new CampHistoricalName
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Name = name.Trim(),
            Source = CampNameSource.Manual,
            CreatedAt = _clock.GetCurrentInstant()
        };

        _dbContext.CampHistoricalNames.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveHistoricalNameAsync(Guid historicalNameId,
        CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.CampHistoricalNames.FindAsync([historicalNameId], cancellationToken)
            ?? throw new InvalidOperationException("Historical name not found.");

        _dbContext.CampHistoricalNames.Remove(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // ==========================================================================
    // Cross-service queries (used by CityPlanningService)
    // ==========================================================================

    public async Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Where(s => s.Id == campSeasonId)
            .Select(s => s.SoundZone)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<string?> GetCampSeasonNameAsync(Guid campSeasonId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Where(s => s.Id == campSeasonId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<CampSeasonInfo?> GetCampSeasonInfoAsync(Guid campSeasonId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Where(s => s.Id == campSeasonId)
            .Select(s => new CampSeasonInfo(s.Id, s.CampId, s.Year))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, CampSeasonDisplayData>> GetCampSeasonDisplayDataForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Include(s => s.Camp)
            .Where(s => s.Year == year)
            .ToDictionaryAsync(
                s => s.Id,
                s => new CampSeasonDisplayData(s.Name, s.Camp.Slug, s.SoundZone),
                cancellationToken);
    }

    public async Task<IReadOnlyList<CampSeasonBrief>> GetCampSeasonBriefsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Include(s => s.Camp)
            .Where(s => s.Year == year)
            .Select(s => new CampSeasonBrief(s.Id, s.Name, s.Camp.Slug))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid?> GetCampLeadSeasonIdForYearAsync(Guid userId, int year,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampLeads
            .Where(l => l.UserId == userId && l.LeftAt == null)
            .Join(_dbContext.CampSeasons,
                l => l.CampId,
                s => s.CampId,
                (l, s) => s)
            .Where(s => s.Year == year)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // ==========================================================================
    // Authorization checks
    // ==========================================================================

    public async Task<bool> IsUserCampLeadAsync(Guid userId, Guid campId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampLeads
            .AnyAsync(l => l.CampId == campId && l.UserId == userId && l.LeftAt == null,
                cancellationToken);
    }


    // ==========================================================================
    // Images
    // ==========================================================================

    public async Task<CampImage> UploadImageAsync(Guid campId, Stream fileStream,
        string fileName, string contentType, long length,
        CancellationToken cancellationToken = default)
    {
        var imageCount = await _dbContext.CampImages
            .CountAsync(i => i.CampId == campId, cancellationToken);
        if (imageCount >= 5)
            throw new InvalidOperationException("Maximum 5 images per camp.");

        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "image/jpeg", "image/png", "image/webp" };
        if (!allowedTypes.Contains(contentType))
            throw new InvalidOperationException("Only JPEG, PNG, and WebP images are allowed.");

        if (length > 10 * 1024 * 1024)
            throw new InvalidOperationException("Image must be under 10MB.");

        var ext = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new InvalidOperationException("Unsupported content type.")
        };
        var storedFileName = $"{Guid.NewGuid()}{ext}";
        var relativePath = Path.Combine("uploads", "camps", campId.ToString(), storedFileName);
        var fullPath = Path.Combine("wwwroot", relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = new FileStream(fullPath, FileMode.Create);
        await fileStream.CopyToAsync(stream, cancellationToken);

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

        _dbContext.CampImages.Add(image);

        await _auditLogService.LogAsync(
            AuditAction.CampImageUploaded, nameof(CampImage), image.Id,
            $"Uploaded image '{fileName}'",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await InvalidateCampYearCachesAsync(campId, cancellationToken);

        return image;
    }

    public async Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        var image = await _dbContext.CampImages.FindAsync([imageId], cancellationToken)
            ?? throw new InvalidOperationException("Image not found.");

        var fullPath = Path.Combine("wwwroot", image.StoragePath);
        if (File.Exists(fullPath)) File.Delete(fullPath);

        _dbContext.CampImages.Remove(image);

        await _auditLogService.LogAsync(
            AuditAction.CampImageDeleted, nameof(CampImage), imageId,
            $"Deleted image '{image.FileName}'",
            "CampService",
            relatedEntityId: image.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await InvalidateCampYearCachesAsync(image.CampId, cancellationToken);
    }

    public async Task ReorderImagesAsync(Guid campId, List<Guid> imageIdsInOrder,
        CancellationToken cancellationToken = default)
    {
        var images = await _dbContext.CampImages
            .Where(i => i.CampId == campId)
            .ToListAsync(cancellationToken);

        for (var i = 0; i < imageIdsInOrder.Count; i++)
        {
            var image = images.FirstOrDefault(img => img.Id == imageIdsInOrder[i]);
            if (image is not null)
                image.SortOrder = i;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await InvalidateCampYearCachesAsync(campId, cancellationToken);
    }

    // ==========================================================================
    // Settings (CampAdmin)
    // ==========================================================================

    public async Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.CampSettings.OrderBy(s => s.Id).FirstAsync(cancellationToken);
        settings.PublicYear = year;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _cache.InvalidateCampSettings();
    }

    public async Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.CampSettings.OrderBy(s => s.Id).FirstAsync(cancellationToken);
        if (!settings.OpenSeasons.Contains(year))
        {
            settings.OpenSeasons.Add(year);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _cache.InvalidateCampSettings();
        }
    }

    public async Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.CampSettings.OrderBy(s => s.Id).FirstAsync(cancellationToken);
        if (settings.OpenSeasons.Remove(year))
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _cache.InvalidateCampSettings();
        }
    }

    public async Task SetNameLockDateAsync(int year, LocalDate lockDate,
        CancellationToken cancellationToken = default)
    {
        var seasons = await _dbContext.CampSeasons
            .Where(s => s.Year == year)
            .ToListAsync(cancellationToken);

        foreach (var season in seasons)
        {
            season.NameLockDate = lockDate;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(year);
    }

    public async Task<Dictionary<int, LocalDate?>> GetNameLockDatesAsync(List<int> years,
        CancellationToken cancellationToken = default)
    {
        var lockDates = await _dbContext.CampSeasons
            .Where(s => years.Contains(s.Year))
            .GroupBy(s => s.Year)
            .Select(g => new { Year = g.Key, LockDate = g.Max(s => s.NameLockDate) })
            .ToDictionaryAsync(x => x.Year, x => x.LockDate, cancellationToken);

        return lockDates;
    }

    // ==========================================================================
    // Name change
    // ==========================================================================

    public async Task ChangeSeasonNameAsync(Guid seasonId, string newName,
        CancellationToken cancellationToken = default)
    {
        var season = await _dbContext.CampSeasons.FindAsync([seasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");

        // Check name lock
        if (season.NameLockDate.HasValue)
        {
            var today = _clock.GetCurrentInstant().InUtc().Date;
            if (today >= season.NameLockDate.Value)
                throw new InvalidOperationException("Season name is locked and cannot be changed.");
        }

        var oldName = season.Name;
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return;

        var now = _clock.GetCurrentInstant();

        // Log old name to history
        _dbContext.CampHistoricalNames.Add(new CampHistoricalName
        {
            Id = Guid.NewGuid(),
            CampId = season.CampId,
            Name = oldName,
            Year = season.Year,
            Source = CampNameSource.NameChange,
            CreatedAt = now
        });

        season.Name = newName;
        season.UpdatedAt = now;

        await _auditLogService.LogAsync(
            AuditAction.CampNameChanged, nameof(CampSeason), seasonId,
            $"Name changed from '{oldName}' to '{newName}'",
            "CampService",
            relatedEntityId: season.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateCache(season.Year);
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
        InvalidateCampYearCaches(await GetCampYearsAsync(campId, cancellationToken));
    }

    private async Task<List<int>> GetCampYearsAsync(Guid campId, CancellationToken cancellationToken)
    {
        return await _dbContext.CampSeasons
            .AsNoTracking()
            .Where(s => s.CampId == campId)
            .Select(s => s.Year)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private void InvalidateCampYearCaches(IEnumerable<int> years)
    {
        foreach (var year in years)
        {
            InvalidateCache(year);
        }
    }

    private static CampSeason CreateSeasonFromData(Guid campId, int year, string name,
        CampSeasonData data, Instant now)
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
    // Camp membership per season
    // ==========================================================================

    /// <summary>
    /// Resolve the "open for membership" season for a camp. Uses the camp's existing
    /// season for <see cref="CampSettings.PublicYear"/>, and only considers Active or Full
    /// seasons eligible (Pending/Rejected/Withdrawn seasons are not open for membership).
    /// </summary>
    private async Task<CampSeason?> ResolveOpenMembershipSeasonAsync(
        Guid campId, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        return await _dbContext.CampSeasons
            .Where(s => s.CampId == campId && s.Year == settings.PublicYear)
            .Where(s => s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var season = await ResolveOpenMembershipSeasonAsync(campId, cancellationToken);
        if (season is null)
        {
            return new CampMemberRequestResult(Guid.Empty, CampMemberRequestOutcome.NoOpenSeason,
                "Camp is not open for membership this year.");
        }

        var existing = await _dbContext.CampMembers
            .Where(m => m.CampSeasonId == season.Id && m.UserId == userId && m.Status != CampMemberStatus.Removed)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing.Status switch
            {
                CampMemberStatus.Pending => new CampMemberRequestResult(existing.Id, CampMemberRequestOutcome.AlreadyPending),
                CampMemberStatus.Active => new CampMemberRequestResult(existing.Id, CampMemberRequestOutcome.AlreadyActive),
                _ => new CampMemberRequestResult(existing.Id, CampMemberRequestOutcome.AlreadyPending)
            };
        }

        var now = _clock.GetCurrentInstant();
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = userId,
            Status = CampMemberStatus.Pending,
            RequestedAt = now
        };

        _dbContext.CampMembers.Add(member);

        await _auditLogService.LogAsync(
            AuditAction.CampMemberRequested, nameof(CampMember), member.Id,
            $"Requested membership in camp season {season.Year}",
            userId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CampMemberRequestResult(member.Id, CampMemberRequestOutcome.Created);
    }

    public async Task ApproveCampMemberAsync(
        Guid campMemberId, Guid approvedByUserId, CancellationToken cancellationToken = default)
    {
        var member = await _dbContext.CampMembers
            .Include(m => m.CampSeason)
            .FirstOrDefaultAsync(m => m.Id == campMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Pending)
            throw new InvalidOperationException($"Cannot approve a camp member with status {member.Status}.");

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Active;
        member.ConfirmedAt = now;
        member.ConfirmedByUserId = approvedByUserId;

        await _auditLogService.LogAsync(
            AuditAction.CampMemberApproved, nameof(CampMember), member.Id,
            $"Approved camp membership for season {member.CampSeason.Year}",
            approvedByUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RejectCampMemberAsync(
        Guid campMemberId, Guid rejectedByUserId, CancellationToken cancellationToken = default)
    {
        var member = await _dbContext.CampMembers
            .Include(m => m.CampSeason)
            .FirstOrDefaultAsync(m => m.Id == campMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Pending)
            throw new InvalidOperationException($"Cannot reject a camp member with status {member.Status}.");

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = rejectedByUserId;

        await _auditLogService.LogAsync(
            AuditAction.CampMemberRejected, nameof(CampMember), member.Id,
            $"Rejected camp membership request for season {member.CampSeason.Year}",
            rejectedByUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveCampMemberAsync(
        Guid campMemberId, Guid removedByUserId, CancellationToken cancellationToken = default)
    {
        var member = await _dbContext.CampMembers
            .Include(m => m.CampSeason)
            .FirstOrDefaultAsync(m => m.Id == campMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Active)
            throw new InvalidOperationException($"Cannot remove a camp member with status {member.Status}.");

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = removedByUserId;

        await _auditLogService.LogAsync(
            AuditAction.CampMemberRemoved, nameof(CampMember), member.Id,
            $"Removed camp member from season {member.CampSeason.Year}",
            removedByUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _dbContext.CampMembers
            .Include(m => m.CampSeason)
            .FirstOrDefaultAsync(m => m.Id == campMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.UserId != userId)
            throw new InvalidOperationException("You can only withdraw your own camp membership request.");

        if (member.Status != CampMemberStatus.Pending)
            throw new InvalidOperationException($"Cannot withdraw a camp member request with status {member.Status}.");

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = userId;

        await _auditLogService.LogAsync(
            AuditAction.CampMemberWithdrawn, nameof(CampMember), member.Id,
            $"Withdrew camp membership request for season {member.CampSeason.Year}",
            userId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _dbContext.CampMembers
            .Include(m => m.CampSeason)
            .FirstOrDefaultAsync(m => m.Id == campMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.UserId != userId)
            throw new InvalidOperationException("You can only leave your own camp membership.");

        if (member.Status != CampMemberStatus.Active)
            throw new InvalidOperationException($"Cannot leave a camp membership with status {member.Status}.");

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = userId;

        await _auditLogService.LogAsync(
            AuditAction.CampMemberLeft, nameof(CampMember), member.Id,
            $"Left camp season {member.CampSeason.Year}",
            userId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<CampMembershipState> GetMembershipStateForCampAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var season = await ResolveOpenMembershipSeasonAsync(campId, cancellationToken);
        if (season is null)
        {
            return new CampMembershipState(null, null, null, CampMemberStatusSummary.NoOpenSeason);
        }

        var member = await _dbContext.CampMembers
            .Where(m => m.CampSeasonId == season.Id && m.UserId == userId && m.Status != CampMemberStatus.Removed)
            .FirstOrDefaultAsync(cancellationToken);

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
        var season = await _dbContext.CampSeasons.FindAsync([campSeasonId], cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");

        var members = await _dbContext.CampMembers
            .Include(m => m.User)
            .Where(m => m.CampSeasonId == campSeasonId && m.Status != CampMemberStatus.Removed)
            .OrderBy(m => m.RequestedAt)
            .ToListAsync(cancellationToken);

        var pending = members
            .Where(m => m.Status == CampMemberStatus.Pending)
            .Select(m => new CampMemberRow(m.Id, m.UserId, m.User.DisplayName, m.RequestedAt, m.ConfirmedAt))
            .ToList();

        var active = members
            .Where(m => m.Status == CampMemberStatus.Active)
            .Select(m => new CampMemberRow(m.Id, m.UserId, m.User.DisplayName, m.RequestedAt, m.ConfirmedAt))
            .ToList();

        return new CampMemberListData(campSeasonId, season.Year, pending, active);
    }

    public async Task<IReadOnlyList<CampMembershipSummary>> GetCampMembershipsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampMembers
            .Include(m => m.CampSeason)
                .ThenInclude(s => s.Camp)
            .Where(m => m.UserId == userId && m.Status != CampMemberStatus.Removed)
            .OrderByDescending(m => m.CampSeason.Year)
            .ThenBy(m => m.CampSeason.Name)
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
            .ToListAsync(cancellationToken);
    }

    // ==========================================================================
    // Camp role definitions and assignments (issue #489)
    // Definition CRUD is implemented; assign/unassign/get-assignments/compliance
    // remain as stubs (see #pragma below) and land in subsequent phases.
    // ==========================================================================

    public async Task<IReadOnlyList<CampRoleDefinitionDto>> GetCampRoleDefinitionsAsync(
        bool includeDeactivated, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.CampRoleDefinitions.AsQueryable();
        if (!includeDeactivated)
            query = query.Where(d => d.DeactivatedAt == null);

        return await query
            .OrderBy(d => d.SortOrder)
            .Select(d => new CampRoleDefinitionDto(d.Id, d.Name, d.Description, d.SlotCount, d.MinimumRequired,
                d.SortOrder, d.IsRequired, d.DeactivatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<CampRoleDefinitionDto> CreateCampRoleDefinitionAsync(
        string name, string? description, int slotCount, int minimumRequired, int sortOrder, bool isRequired,
        Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        if (slotCount < 1) throw new ArgumentOutOfRangeException(nameof(slotCount), "SlotCount must be >= 1.");
        if (minimumRequired < 0 || minimumRequired > slotCount)
            throw new ArgumentOutOfRangeException(nameof(minimumRequired), "MinimumRequired must be 0..SlotCount.");

        if (await _dbContext.CampRoleDefinitions.AnyAsync(d => d.Name == name, cancellationToken))
            throw new InvalidOperationException($"A camp role definition named '{name}' already exists.");

        var now = _clock.GetCurrentInstant();
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description,
            SlotCount = slotCount,
            MinimumRequired = minimumRequired,
            SortOrder = sortOrder,
            IsRequired = isRequired,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _dbContext.CampRoleDefinitions.Add(def);
        await _auditLogService.LogAsync(
            AuditAction.CampRoleDefinitionCreated,
            entityType: nameof(CampRoleDefinition),
            entityId: def.Id,
            description: $"Created camp role definition '{def.Name}' (slots={def.SlotCount}, min={def.MinimumRequired}, required={def.IsRequired}).",
            actorUserId: actorUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CampRoleDefinitionDto(def.Id, def.Name, def.Description, def.SlotCount, def.MinimumRequired,
            def.SortOrder, def.IsRequired, def.DeactivatedAt);
    }

    public async Task<CampRoleDefinitionDto> UpdateCampRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount, int minimumRequired, int sortOrder, bool isRequired,
        Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var def = await _dbContext.CampRoleDefinitions
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException("Role definition not found.");

        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        if (slotCount < 1) throw new ArgumentOutOfRangeException(nameof(slotCount));
        if (minimumRequired < 0 || minimumRequired > slotCount)
            throw new ArgumentOutOfRangeException(nameof(minimumRequired));

        if (await _dbContext.CampRoleDefinitions.AnyAsync(d => d.Id != roleDefinitionId && d.Name == name, cancellationToken))
            throw new InvalidOperationException($"Another role definition named '{name}' already exists.");

        def.Name = name.Trim();
        def.Description = description;
        def.SlotCount = slotCount;
        def.MinimumRequired = minimumRequired;
        def.SortOrder = sortOrder;
        def.IsRequired = isRequired;
        def.UpdatedAt = _clock.GetCurrentInstant();

        await _auditLogService.LogAsync(
            AuditAction.CampRoleDefinitionUpdated,
            entityType: nameof(CampRoleDefinition),
            entityId: def.Id,
            description: $"Updated camp role definition '{def.Name}'.",
            actorUserId: actorUserId);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CampRoleDefinitionDto(def.Id, def.Name, def.Description, def.SlotCount, def.MinimumRequired,
            def.SortOrder, def.IsRequired, def.DeactivatedAt);
    }

    public async Task DeactivateCampRoleDefinitionAsync(Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var def = await _dbContext.CampRoleDefinitions.FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException("Role definition not found.");

        if (def.DeactivatedAt is null)
        {
            def.DeactivatedAt = _clock.GetCurrentInstant();
            def.UpdatedAt = def.DeactivatedAt.Value;
            await _auditLogService.LogAsync(
                AuditAction.CampRoleDefinitionDeactivated,
                entityType: nameof(CampRoleDefinition),
                entityId: def.Id,
                description: $"Deactivated camp role definition '{def.Name}'.",
                actorUserId: actorUserId);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ReactivateCampRoleDefinitionAsync(Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var def = await _dbContext.CampRoleDefinitions.FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException("Role definition not found.");

        if (def.DeactivatedAt is not null)
        {
            def.DeactivatedAt = null;
            def.UpdatedAt = _clock.GetCurrentInstant();
            await _auditLogService.LogAsync(
                AuditAction.CampRoleDefinitionReactivated,
                entityType: nameof(CampRoleDefinition),
                entityId: def.Id,
                description: $"Reactivated camp role definition '{def.Name}'.",
                actorUserId: actorUserId);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<AssignCampRoleResult> AssignCampRoleAsync(
        Guid campSeasonId, Guid campRoleDefinitionId, int slotIndex,
        Guid assigneeUserId, Guid assignedByUserId, bool autoPromoteToMember,
        CancellationToken cancellationToken = default)
    {
        var def = await _dbContext.CampRoleDefinitions
            .FirstOrDefaultAsync(d => d.Id == campRoleDefinitionId, cancellationToken);
        if (def is null)
            return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.RoleDeactivated, ErrorMessage: "Role not found.");
        if (def.DeactivatedAt is not null)
            return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.RoleDeactivated, ErrorMessage: "Role is deactivated.");
        if (slotIndex < 0 || slotIndex >= def.SlotCount)
            return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.SlotIndexOutOfRange, ErrorMessage: $"Slot {slotIndex} is out of range for {def.Name}.");

        var season = await _dbContext.CampSeasons
            .Include(s => s.Camp)
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, cancellationToken);
        if (season is null || (season.Status != CampSeasonStatus.Active && season.Status != CampSeasonStatus.Full))
            return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.NoOpenSeason, ErrorMessage: "Season is not open for membership.");

        // Slot occupancy
        if (await _dbContext.CampRoleAssignments.AnyAsync(a =>
                a.CampSeasonId == campSeasonId && a.CampRoleDefinitionId == campRoleDefinitionId && a.SlotIndex == slotIndex, cancellationToken))
            return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.SlotOccupied, ErrorMessage: "Slot is already filled.");

        // Resolve or promote CampMember
        var member = await _dbContext.CampMembers
            .FirstOrDefaultAsync(m => m.CampSeasonId == campSeasonId && m.UserId == assigneeUserId && m.Status != CampMemberStatus.Removed,
                cancellationToken);
        var outcome = AssignCampRoleOutcome.Assigned;
        var now = _clock.GetCurrentInstant();

        if (member is null)
        {
            // No row, or only a Removed row (filtered out above).
            if (!autoPromoteToMember)
                return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.InvalidUser, ErrorMessage: "User is not an active member of this season.");
            member = new CampMember
            {
                Id = Guid.NewGuid(),
                CampSeasonId = campSeasonId,
                UserId = assigneeUserId,
                Status = CampMemberStatus.Active,
                RequestedAt = now,
                ConfirmedAt = now,
                ConfirmedByUserId = assignedByUserId,
            };
            _dbContext.CampMembers.Add(member);
            await _auditLogService.LogAsync(
                AuditAction.CampMemberApproved,
                entityType: nameof(CampMember),
                entityId: member.Id,
                description: $"Auto-promoted user {assigneeUserId} to Active for camp '{season.Camp.Slug}' season {season.Year} via role assignment.",
                actorUserId: assignedByUserId);
            outcome = AssignCampRoleOutcome.AssignedWithAutoPromote;
        }
        else if (member.Status == CampMemberStatus.Pending)
        {
            if (!autoPromoteToMember)
                return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.InvalidUser, ErrorMessage: "User has only a pending request — confirm first.");
            member.Status = CampMemberStatus.Active;
            member.ConfirmedAt = now;
            member.ConfirmedByUserId = assignedByUserId;
            await _auditLogService.LogAsync(
                AuditAction.CampMemberApproved,
                entityType: nameof(CampMember),
                entityId: member.Id,
                description: $"Auto-approved pending request for user {assigneeUserId} via role assignment.",
                actorUserId: assignedByUserId);
            outcome = AssignCampRoleOutcome.AssignedWithAutoPromote;
        }

        // Within-role uniqueness — same human can't hold two slots of one role
        if (await _dbContext.CampRoleAssignments.AnyAsync(a =>
                a.CampSeasonId == campSeasonId && a.CampRoleDefinitionId == campRoleDefinitionId && a.CampMemberId == member.Id, cancellationToken))
            return new AssignCampRoleResult(Guid.Empty, AssignCampRoleOutcome.AlreadyHoldsRole, ErrorMessage: "This human already holds this role for this season.");

        var assignment = new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampRoleDefinitionId = def.Id,
            CampSeasonId = season.Id,
            CampMemberId = member.Id,
            SlotIndex = slotIndex,
            AssignedAt = now,
            AssignedByUserId = assignedByUserId,
        };
        _dbContext.CampRoleAssignments.Add(assignment);

        await _auditLogService.LogAsync(
            AuditAction.CampRoleAssigned,
            entityType: nameof(CampRoleAssignment),
            entityId: assignment.Id,
            description: $"Assigned slot {slotIndex + 1} of '{def.Name}' to member {member.Id} for camp '{season.Camp.Slug}' season {season.Year}.",
            actorUserId: assignedByUserId);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new AssignCampRoleResult(
            AssignmentId: assignment.Id,
            Outcome: outcome,
            AssigneeUserId: assigneeUserId,
            RoleName: def.Name,
            CampSlug: season.Camp.Slug,
            CampName: season.Name);
    }

#pragma warning disable MA0025 // Implement the functionality (intentional stubs landing in follow-up commits)
    public Task<IReadOnlyList<CampRoleAssignmentDto>> GetCampRoleAssignmentsAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task UnassignCampRoleAsync(Guid assignmentId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<CampComplianceRow>> GetCampRoleComplianceAsync(int year, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
#pragma warning restore MA0025
}
