using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Helpers;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Camps;

/// <summary>Application-layer <see cref="ICampService"/>; cache-unaware (decorator owns §15 caching).</summary>
public sealed class CampService : ICampService, ICampRoleCampAccess, IUserDataContributor, IUserMerge
{
    private readonly ICampRepository _repo;
    private readonly IUserServiceRead _userService;
    private readonly IAuditLogService _auditLog;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly IFileStorage _fileStorage;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly ICampLeadJoinRequestsBadgeCacheInvalidator _leadBadgeInvalidator;
    private readonly Lazy<ICampRoleService> _campRoleService;
    private readonly IEarlyEntryInvalidator _earlyEntryInvalidator;
    private readonly IClock _clock;
    private readonly ILogger<CampService> _logger;

    private static readonly HashSet<string> AllowedImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    public CampService(
        ICampRepository repo,
        IUserServiceRead userService,
        IAuditLogService auditLog,
        ISystemTeamSync systemTeamSync,
        IFileStorage fileStorage,
        INotificationEmitter notificationEmitter,
        ICampLeadJoinRequestsBadgeCacheInvalidator leadBadgeInvalidator,
        Lazy<ICampRoleService> campRoleService,
        IEarlyEntryInvalidator earlyEntryInvalidator,
        IClock clock,
        ILogger<CampService> logger)
    {
        _repo = repo;
        _userService = userService;
        _auditLog = auditLog;
        _systemTeamSync = systemTeamSync;
        _fileStorage = fileStorage;
        _notificationEmitter = notificationEmitter;
        _leadBadgeInvalidator = leadBadgeInvalidator;
        _campRoleService = campRoleService;
        _earlyEntryInvalidator = earlyEntryInvalidator;
        _clock = clock;
        _logger = logger;
    }

    // --- Registration ---

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

        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = createdByUserId,
            Status = CampMemberStatus.Active,
            RequestedAt = now,
            ConfirmedAt = now,
            ConfirmedByUserId = createdByUserId,
        };

        var leadDef = await _repo.GetSpecialDefinitionAsync(CampSpecialRole.Lead, cancellationToken);
        CampRoleAssignment? leadAssignment = null;
        if (leadDef is not null)
        {
            leadAssignment = new CampRoleAssignment
            {
                Id = Guid.NewGuid(),
                CampSeasonId = season.Id,
                CampRoleDefinitionId = leadDef.Id,
                CampMemberId = member.Id,
                AssignedAt = now,
                AssignedByUserId = createdByUserId,
            };
        }
        else
        {
            _logger.LogWarning(
                "Camp Lead role definition missing while creating camp {CampId}; creator added as Active member without a lead assignment. Run 'Seed system roles'.",
                camp.Id);
        }

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

        await _repo.CreateCampAsync(camp, season, member, leadAssignment, historicalNameEntities, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampCreated, nameof(Camp), camp.Id,
            $"Registered camp '{name}' for {year}",
            createdByUserId);

        await _systemTeamSync.SyncMembershipForUserAsync(
            createdByUserId, SystemTeamType.BarrioLeads, cancellationToken);

        return camp;
    }

    // --- Queries ---

    public async Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetBySlugAsync(slug, cancellationToken);
        // GetBySlugAsync does not load Seasons.Members, so EE/member counts are
        // unknown here — emit null rather than a misleading 0.
        if (camp is null) return null;
        var specialRoleUserIds = await GetSpecialRoleUserIdsBySeasonAsync(
            camp.Seasons.Select(season => season.Year).Distinct().ToList(),
            cancellationToken);
        return CreateCampInfo(camp, includeEarlyEntryGrantCount: false, specialRoleUserIds);
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

        return CreateCampEditData(
            camp,
            season,
            _clock.GetCurrentInstant().InUtc().Date);
    }

    public async Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var camps = await _repo.GetCampsWithLeadsForYearAsync(
            year, statusFilter: null, cancellationToken);
        var specialRoleUserIds = await GetSpecialRoleUserIdsBySeasonAsync([year], cancellationToken);
        return camps.Select(c => CreateCampInfo(c, specialRoleUserIds: specialRoleUserIds)).ToList();
    }

    private async Task<List<Camp>> GetCampEntitiesForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var camps = await _repo.GetAllCampsForYearAsync(year, cancellationToken);
        return camps.ToList();
    }

    public async Task<IReadOnlyList<(Guid CampId, string CampName, string CampSlug, Guid CampSeasonId)>>
        GetCampSeasonsForComplianceAsync(int year, CancellationToken cancellationToken = default)
    {
        var camps = await GetCampEntitiesForYearAsync(year, cancellationToken);
        // Canonical name lives on CampSeason (per-season), not Camp.
        return camps.SelectMany(c => c.Seasons.Where(s => s.Year == year).Select(s =>
            (c.Id, s.Name, c.Slug, s.Id))).ToList();
    }

    public async Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(cancellationToken);
        if (settings is null)
            throw new InvalidOperationException("Camp settings not found.");

        var info = CreateCampSettingsInfo(settings);
        if (info.OpenSeasons.Count == 0)
        {
            return info;
        }

        var nameLockDates = await _repo.GetNameLockDatesAsync(info.OpenSeasons, cancellationToken);
        return info with { NameLockDates = nameLockDates.ToDictionary(kv => kv.Key, kv => kv.Value) };
    }

    public async Task<IReadOnlyList<CampSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var year = settings.PublicYear;

        if (Guid.TryParse(query, out var id))
        {
            var camp = await _repo.GetByIdAsync(id, cancellationToken);
            if (camp is null) return [];
            var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
            return [new CampSearchHit(camp.Slug, season?.Name ?? camp.Slug)];
        }

        var camps = await _repo.SearchForYearAsync(
            query, year, onlyPublicStatus: true, max, cancellationToken);

        var hits = new List<CampSearchHit>(camps.Count);
        foreach (var camp in camps)
        {
            var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
            var name = season?.Name ?? camp.Slug;
            hits.Add(new CampSearchHit(camp.Slug, name));
        }
        return hits;
    }

    private async Task<IReadOnlyDictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>>>
        GetSpecialRoleUserIdsBySeasonAsync(IReadOnlyCollection<int> years, CancellationToken cancellationToken)
    {
        if (years.Count == 0) return new Dictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>>();

        var assignments = await _repo.GetActiveAssignmentsForYearsAsync(years, cancellationToken);
        return assignments
            .Where(a => a.Definition.SpecialRole is CampSpecialRole.Lead or CampSpecialRole.Workshop)
            .GroupBy(a => (a.CampSeasonId, a.Definition.SpecialRole))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group
                    .Select(a => a.CampMember.UserId)
                    .Distinct()
                    .ToList());
    }

    private static CampInfo CreateCampInfo(
        Camp camp,
        bool includeEarlyEntryGrantCount = true,
        IReadOnlyDictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>>? specialRoleUserIds = null)
    {
        return new CampInfo(
            camp.Id,
            camp.Slug,
            camp.ContactEmail,
            camp.ContactPhone,
            camp.IsSwissCamp,
            camp.TimesAtNowhere,
            camp.Seasons
                .Select(s => CreateCampSeasonInfo(s, camp.Slug, includeEarlyEntryGrantCount, specialRoleUserIds))
                .ToList())
        {
            WebOrSocialUrl = camp.WebOrSocialUrl,
            Links = camp.Links,
            HideHistoricalNames = camp.HideHistoricalNames,
            HistoricalNames = camp.HistoricalNames
                .Select(name => name.Name)
                .ToList(),
            Images = camp.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => new CampImageSummary(image.Id, $"/{image.StoragePath}", image.SortOrder))
                .ToList()
        };
    }

    private static CampSeasonInfo CreateCampSeasonInfo(
        CampSeason season,
        string campSlug,
        bool includeEarlyEntryGrantCount = false,
        IReadOnlyDictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>>? specialRoleUserIds = null)
    {
        return new CampSeasonInfo(
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
            includeEarlyEntryGrantCount
                ? season.Members.Count(m => m.Status == CampMemberStatus.Active && m.HasEarlyEntry)
                : null,
            includeEarlyEntryGrantCount
                ? season.Members.Count(m => m.Status == CampMemberStatus.Active)
                : null)
        {
            BlurbLong = season.BlurbLong,
            KidsVisiting = season.KidsVisiting,
            KidsAreaDescription = season.KidsAreaDescription,
            HasPerformanceSpace = season.HasPerformanceSpace,
            PerformanceTypes = season.PerformanceTypes,
            Members = season.Members
                .Where(m => m.Status != CampMemberStatus.Removed)
                .OrderBy(m => m.RequestedAt)
                .Select(CreateCampSeasonMemberInfo)
                .ToList(),
            LeadUserIds = GetSpecialRoleUserIds(season.Id, CampSpecialRole.Lead, specialRoleUserIds),
            WorkshopLeadUserIds = GetSpecialRoleUserIds(season.Id, CampSpecialRole.Workshop, specialRoleUserIds)
        };
    }

    private static IReadOnlyList<Guid> GetSpecialRoleUserIds(
        Guid campSeasonId,
        CampSpecialRole role,
        IReadOnlyDictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>>? specialRoleUserIds)
    {
        return specialRoleUserIds is not null
               && specialRoleUserIds.TryGetValue((campSeasonId, role), out var userIds)
            ? userIds
            : [];
    }

    private static CampSettingsInfo CreateCampSettingsInfo(CampSettings settings) =>
        new(
            settings.PublicYear,
            settings.OpenSeasons.ToList(),
            settings.EeStartDate);

    private static CampSeasonMemberInfo CreateCampSeasonMemberInfo(CampMember member) =>
        new(
            member.Id,
            member.UserId,
            member.Status,
            member.RequestedAt,
            member.ConfirmedAt,
            member.HasEarlyEntry);

    private static CampEditData CreateCampEditData(Camp camp, CampSeason season, LocalDate today)
    {
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
            season.ElectricalGrid,
            camp.Images
                .OrderBy(i => i.SortOrder)
                .Select(i => new CampImageSummary(i.Id, $"/{i.StoragePath}", i.SortOrder))
                .ToList(),
            camp.HistoricalNames
                .Select(h => new CampHistoricalNameSummary(h.Id, h.Name, h.Year, h.Source.ToString()))
                .ToList());
    }

    // --- Season management ---

    public async Task<CampSeason> OptInToSeasonAsync(
        Guid campId, int year, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.OpenSeasons.Contains(year))
        {
            throw new InvalidOperationException($"Season {year} is not open for registration.");
        }

        if (await _repo.SeasonExistsAsync(campId, year, cancellationToken))
        {
            throw new InvalidOperationException($"Camp already has a season for {year}.");
        }

        var previousSeason = await _repo.GetLatestSeasonAsync(campId, cancellationToken)
            ?? throw new InvalidOperationException("No previous season to copy from.");

        var hasApprovedSeason = await _repo.HasApprovedSeasonAsync(campId, cancellationToken);

        var now = _clock.GetCurrentInstant();
        var newSeason = hasApprovedSeason
            ? previousSeason.CreateApprovedRenewal(Guid.NewGuid(), year, now)
            : previousSeason.CreatePendingRenewal(Guid.NewGuid(), year, now);

        await _repo.AddSeasonAsync(newSeason, cancellationToken);

        await _auditLog.LogAsync(
            AuditAction.CampSeasonCreated, nameof(CampSeason), newSeason.Id,
            $"Opted in to season {year} (auto-approved: {hasApprovedSeason})",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

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

    }

    public async Task ApproveSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            season.Approve(reviewedByUserId, notes, now);
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

    }

    public async Task RejectSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            season.Reject(reviewedByUserId, notes, now);
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

    }

    public async Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            season.Withdraw(now);
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

    }

    private async Task NotifyPendingRequestersOfSeasonClosureAsync(
        Guid seasonId, Guid campId, int year, CancellationToken cancellationToken)
    {
        var pendingUserIds = await _repo.GetPendingRequesterUserIdsForSeasonAsync(seasonId, cancellationToken);
        if (pendingUserIds.Count == 0)
        {
            return;
        }

        // Closing a season drops pending requests from the lead-meter count.
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

    public async Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;
        var previousStatus = CampSeasonStatus.Pending;
        var newStatus = CampSeasonStatus.Pending;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            previousStatus = season.Status;
            newStatus = season.Reactivate(now);
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

    }

    // --- Camp updates ---

    public async Task<CampUpdateResult> UpdateCampAsync(
        CampUpdateInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await _repo.UpdateCampFieldsAsync(
                input.CampId,
                input.ContactEmail,
                input.ContactPhone,
                input.WebOrSocialUrl,
                input.Links,
                input.IsSwissCamp,
                input.TimesAtNowhere,
                input.HideHistoricalNames,
                _clock.GetCurrentInstant(),
                cancellationToken);

            if (!updated)
            {
                return CampUpdateResult.Failure("Camp not found.");
            }

            await _auditLog.LogAsync(
                AuditAction.CampUpdated, nameof(Camp), input.CampId,
                $"Updated camp {input.CampId}",
                "CampService");

            await UpdateSeasonAsync(input.SeasonId, input.SeasonData, cancellationToken);
            await ChangeSeasonNameIfAllowedAsync(input.SeasonId, input.SeasonName, cancellationToken);

            return CampUpdateResult.Success();
        }
        catch (InvalidOperationException ex)
        {
            return CampUpdateResult.Failure(ex.Message);
        }
    }

    private async Task ChangeSeasonNameIfAllowedAsync(
        Guid seasonId,
        string seasonName,
        CancellationToken cancellationToken)
    {
        var currentSeason = await _repo.GetSeasonByIdAsync(seasonId, cancellationToken)
            ?? throw new InvalidOperationException("Season not found.");

        if (string.Equals(currentSeason.Name, seasonName, StringComparison.Ordinal))
        {
            return;
        }

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var nameLocked = currentSeason.NameLockDate.HasValue && today >= currentSeason.NameLockDate.Value;
        if (nameLocked)
        {
            return;
        }

        await ChangeSeasonNameAsync(currentSeason.Id, seasonName, cancellationToken);
    }

    public async Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        var deletedImagePaths = await _repo.DeleteCampAsync(campId, cancellationToken);
        if (deletedImagePaths is null)
        {
            throw new InvalidOperationException("Camp not found.");
        }

        foreach (var path in deletedImagePaths)
        {
            try
            {
                await _fileStorage.DeleteAsync(path, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to delete camp image file at {StoragePath} during camp delete for {CampId}; DB row already removed",
                    path, campId);
            }
        }

        await _auditLog.LogAsync(
            AuditAction.CampDeleted, nameof(Camp), campId,
            $"Camp {campId} permanently deleted",
            "CampService");

    }

    // --- Historical names ---

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

    // --- Cross-service queries (used by CityPlanningService) ---

    public async Task<CampSeasonInfo?> GetCampSeasonByIdAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var season = await _repo.GetSeasonByIdAsync(campSeasonId, cancellationToken);
        if (season is null) return null;
        var specialRoleUserIds = await GetSpecialRoleUserIdsBySeasonAsync([season.Year], cancellationToken);
        return CreateCampSeasonInfo(
            season,
            season.Camp?.Slug ?? string.Empty,
            specialRoleUserIds: specialRoleUserIds);
    }

    public async Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken cancellationToken = default)
    {
        var row = await _repo.GetMemberLookupAsync(campMemberId, cancellationToken);
        return row is null ? null : new CampMemberLookup(row.Value.CampSeasonId, row.Value.UserId, row.Value.Status);
    }

    // --- Images ---

    public async Task<CampImageUploadResult> UploadImageAsync(
        Guid campId, Stream fileStream, string fileName, string contentType, long length,
        CancellationToken cancellationToken = default)
    {
        var imageCount = await _repo.CountImagesAsync(campId, cancellationToken);
        if (imageCount >= 5)
        {
            return CampImageUploadResult.Failure("Maximum 5 images per camp.");
        }

        if (!AllowedImageContentTypes.Contains(contentType))
        {
            return CampImageUploadResult.Failure("Only JPEG, PNG, and WebP images are allowed.");
        }

        if (length > 10 * 1024 * 1024)
        {
            return CampImageUploadResult.Failure("Image must be under 10MB.");
        }

        // Security: extension whitelist prevents image/jpeg + .html (static middleware would serve as HTML).
        var ext = Path.GetExtension(fileName);
        if (!AllowedImageExtensions.Contains(ext))
        {
            return CampImageUploadResult.Failure("Image filename must end in .jpg, .jpeg, .png, or .webp.");
        }
        var storageKey = $"uploads/camps/{campId}/{Guid.NewGuid()}{ext}";
        await _fileStorage.SaveAsync(storageKey, fileStream, cancellationToken);

        var image = new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            FileName = fileName,
            StoragePath = storageKey,
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

        return CampImageUploadResult.Success(image);
    }

    public async Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        var result = await _repo.DeleteImageAsync(imageId, cancellationToken)
            ?? throw new InvalidOperationException("Image not found.");

        try
        {
            await _fileStorage.DeleteAsync(result.StoragePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to delete camp image file at {StoragePath} for image {ImageId}; DB row already removed",
                result.StoragePath, imageId);
        }

        await _auditLog.LogAsync(
            AuditAction.CampImageDeleted, nameof(CampImage), imageId,
            $"Deleted image {imageId}",
            "CampService",
            relatedEntityId: result.CampId, relatedEntityType: nameof(Camp));

    }

    public async Task ReorderImagesAsync(
        Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default)
    {
        await _repo.ReorderImagesAsync(campId, imageIdsInOrder, cancellationToken);
    }

    // --- Settings (CampAdmin) ---

    public async Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default)
    {
        await _repo.SetPublicYearAsync(year, cancellationToken);
    }

    public async Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        await _repo.OpenSeasonAsync(year, cancellationToken);
    }

    public async Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default)
    {
        await _repo.CloseSeasonAsync(year, cancellationToken);
    }

    public async Task SetNameLockDateAsync(
        int year, LocalDate lockDate, CancellationToken cancellationToken = default)
    {
        await _repo.SetNameLockDateForYearAsync(year, lockDate, cancellationToken);
    }

    // --- Name change ---

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
            return;
        }

        await _auditLog.LogAsync(
            AuditAction.CampNameChanged, nameof(CampSeason), seasonId,
            $"Name changed from '{oldName}' to '{newName}'",
            "CampService",
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

    }

    // --- Private helpers ---

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
            Vibes = [.. data.Vibes],
            AdultPlayspace = data.AdultPlayspace,
            MemberCount = data.MemberCount,
            SpaceRequirement = data.SpaceRequirement,
            SoundZone = data.SoundZone,
            ElectricalGrid = data.ElectricalGrid,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    // --- Camp membership per season (see nobodies-collective/Humans#488) ---

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
        // Leads come from the role system (Camp Lead special role on each season).
        var leadUserIds = new HashSet<Guid>();
        foreach (var season in camp.Seasons)
        {
            foreach (var leadUserId in await _repo.GetSpecialRoleHolderUserIdsForSeasonAsync(
                season.Id, CampSpecialRole.Lead, cancellationToken))
            {
                leadUserIds.Add(leadUserId);
            }
        }
        foreach (var leadUserId in leadUserIds)
        {
            _leadBadgeInvalidator.Invalidate(leadUserId);
        }
    }

    /// <summary>Sole CampMember→Removed transition: role-cascade, state flip, audit. Callers own preconditions and post-effects.</summary>
    private async Task TransitionMemberToRemovedAsync(
        CampMember member,
        Guid actorUserId,
        AuditAction auditAction,
        string auditMessage,
        bool cascadeRoleAssignments,
        CancellationToken cancellationToken)
    {
        if (cascadeRoleAssignments)
        {
            await _campRoleService.Value.RemoveAllForMemberAsync(
                member.Id, actorUserId, cancellationToken);
        }

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = actorUserId;
        member.HasEarlyEntry = false;
        await _repo.SaveMemberAsync(member, cancellationToken);
        _earlyEntryInvalidator.InvalidateUser(member.UserId);

        await _auditLog.LogAsync(
            auditAction, nameof(CampMember), member.Id,
            auditMessage, actorUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));
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
                "Camp is not open for membership this year.",
                CampMemberRequestNoticeLevel.Error);
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
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.Created,
                    "Your request to join has been sent to the camp leads.",
                    CampMemberRequestNoticeLevel.Success),
            CampMemberInsertOutcome.AlreadyActive =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.AlreadyActive,
                    "You are already an active member of this camp.",
                    CampMemberRequestNoticeLevel.Info),
            _ =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.AlreadyPending,
                    "You already have a pending request for this camp.",
                    CampMemberRequestNoticeLevel.Info)
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
            throw new InvalidOperationException($"Cannot reject a camp member with status {member.Status}.");

        var requesterUserId = member.UserId;
        var seasonId = member.CampSeasonId;

        await TransitionMemberToRemovedAsync(
            member, rejectedByUserId,
            AuditAction.CampMemberRejected,
            $"Rejected camp membership request for season {member.CampSeason.Year}",
            cascadeRoleAssignments: false,
            cancellationToken);

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
            throw new InvalidOperationException($"Cannot remove a camp member with status {member.Status}.");

        await TransitionMemberToRemovedAsync(
            member, removedByUserId,
            AuditAction.CampMemberRemoved,
            $"Removed camp member from season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);
    }

    private async Task<Guid> EnsureActiveCampMemberAsync(Guid campSeasonId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var result = await _repo.AddActiveMembershipAsync(campSeasonId, userId, now, actorUserId, cancellationToken);

        if (result.Outcome != CampMemberInsertOutcome.AlreadyActive)
        {
            await _auditLog.LogAsync(
                AuditAction.CampMemberAddedByLead,
                nameof(CampMember), result.MemberId,
                "Lead added human as active camp member.",
                actorUserId,
                relatedEntityId: userId, relatedEntityType: nameof(User));
        }

        return result.MemberId;
    }

    public Task<Guid> EnsureActiveMemberForMigrationAsync(
        Guid campSeasonId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        EnsureActiveCampMemberAsync(campSeasonId, userId, actorUserId, cancellationToken);

    public async Task<AddCampMemberOutcome> AddCampMemberToActiveSeasonAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return AddCampMemberOutcome.InvalidUser;

        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        var openSeason = camp?.Seasons.FirstOrDefault(s => s.Status == CampSeasonStatus.Active);
        if (openSeason is null)
            return AddCampMemberOutcome.NoActiveSeason;

        await EnsureActiveCampMemberAsync(openSeason.Id, userId, actorUserId, cancellationToken);
        return AddCampMemberOutcome.Added;
    }

    private async Task<AssignCampRoleOutcome> AddMemberAndAssignRoleAsync(
        Guid campSeasonId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var memberId = await EnsureActiveCampMemberAsync(campSeasonId, userId, actorUserId, cancellationToken);
        return await _campRoleService.Value.AssignAsync(
            campSeasonId, roleDefinitionId, memberId, actorUserId, cancellationToken);
    }

    public async Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        var openSeason = camp?.Seasons.FirstOrDefault(s => s.Status == CampSeasonStatus.Active);
        if (openSeason is null)
            return AssignCampRoleOutcome.SeasonNotFound;

        return await AddMemberAndAssignRoleAsync(
            openSeason.Id, roleDefinitionId, userId, actorUserId, cancellationToken);
    }

    public async Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Camp member record not found.");

        if (member.Status != CampMemberStatus.Pending)
            throw new InvalidOperationException($"Cannot withdraw a camp member request with status {member.Status}.");

        await TransitionMemberToRemovedAsync(
            member, userId,
            AuditAction.CampMemberWithdrawn,
            $"Withdrew camp membership request for season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);

        await InvalidateLeadBadgesAsync(member.CampSeason.CampId, cancellationToken);
    }

    public async Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken);
        if (member is null)
        {
            return CampMembershipMutationResult.Failure("Camp member record not found.");
        }

        if (member.Status != CampMemberStatus.Active)
        {
            return CampMembershipMutationResult.Failure($"Cannot leave a camp membership with status {member.Status}.");
        }

        await TransitionMemberToRemovedAsync(
            member, userId,
            AuditAction.CampMemberLeft,
            $"Left camp season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);

        return CampMembershipMutationResult.Success();
    }

    // --- Account-merge fold ---

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        // AccountMergeService.AcceptAsync wraps the save in its TransactionScope.
        // Camp Lead is a CampRoleAssignment now, so the role-side reassignment moves leads too.
        await _repo.ReassignAssignmentsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

        // Lead moves change Barrio Leads team membership + lead-badge cache for both users.
        await _systemTeamSync.SyncMembershipForUserAsync(sourceUserId, SystemTeamType.BarrioLeads, ct);
        await _systemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.BarrioLeads, ct);
        _leadBadgeInvalidator.Invalidate(sourceUserId);
        _leadBadgeInvalidator.Invalidate(targetUserId);
    }

    // --- IUserDataContributor — GDPR export ---

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        // Camp Lead is now a CampRoleAssignment. The legacy camp_leads table still
        // holds per-user rows until #774 drops it, so Article 15 export must keep
        // including them alongside the role-assignment slice (design-rules §8a).
        var legacyLeads = await _repo.GetAllLeadAssignmentsForUserAsync(userId, ct);
        var shapedLeads = legacyLeads.Select(cl => new
        {
            CampSlug = cl.Camp.Slug,
            cl.Role,
            JoinedAt = cl.JoinedAt.ToInvariantInstantString(),
            LeftAt = cl.LeftAt.ToInvariantInstantString()
        }).ToList();

        var roleAssignments = await _repo.GetAllAssignmentsForUserAsync(userId, ct);

        var shapedRoles = roleAssignments.Select(a => new
        {
            CampSlug = a.CampSeason.Camp.Slug,
            SeasonYear = a.CampSeason.Year,
            RoleName = a.Definition.Name,
            AssignedAt = a.AssignedAt.ToInvariantInstantString(),
            a.AssignedByUserId
        }).ToList();

        return
        [
            new UserDataSlice(GdprExportSections.CampLeadAssignments, shapedLeads),
            new UserDataSlice(GdprExportSections.CampRoleAssignments, shapedRoles)
        ];
    }

    public async Task SetEeStartDateAsync(
        LocalDate? eeStartDate, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await _repo.SetEeStartDateAsync(eeStartDate, cancellationToken);
        var settings = await _repo.GetSettingsReadOnlyAsync(cancellationToken)
            ?? throw new InvalidOperationException("Camp settings not found.");
        await _auditLog.LogAsync(
            AuditAction.CampSettingsEeStartDateChanged,
            nameof(CampSettings), settings.Id,
            eeStartDate is null
                ? "EE start date cleared."
                : $"EE start date set to {eeStartDate.Value:yyyy-MM-dd}.",
            actorUserId);

        // Global date change shifts EE for every camp holder at once.
        _earlyEntryInvalidator.InvalidateAll();
    }

    public async Task SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (slotCount < 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "EE slot count cannot be negative.");

        var result = await _repo.SetCampSeasonEeSlotCountAsync(campSeasonId, slotCount, cancellationToken);
        if (result is null)
            throw new InvalidOperationException("Camp season not found.");

        var (oldValue, newValue, campId) = result.Value;
        if (oldValue == newValue) return;

        await _auditLog.LogAsync(
            AuditAction.CampSeasonEeSlotCountChanged,
            nameof(CampSeason), campSeasonId,
            $"EE slot count changed from {oldValue} to {newValue}.",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
    }

    public async Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
        Guid scopedCampId, Guid campMemberId, bool granted, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return SetEarlyEntryOutcome.MemberNotFound;

        if (member.HasEarlyEntry == granted)
            return SetEarlyEntryOutcome.NoChange;

        if (granted)
        {
            if (member.Status != CampMemberStatus.Active)
                return SetEarlyEntryOutcome.MemberNotActive;

            var current = await _repo.GetGrantedCountForSeasonAsync(member.CampSeasonId, cancellationToken);
            if (current >= member.CampSeason.EeSlotCount)
                return SetEarlyEntryOutcome.SlotCapExceeded;
        }

        member.HasEarlyEntry = granted;
        await _repo.SaveMemberAsync(member, cancellationToken);
        _earlyEntryInvalidator.InvalidateUser(member.UserId);

        await _auditLog.LogAsync(
            granted ? AuditAction.CampEarlyEntryGranted : AuditAction.CampEarlyEntryRevoked,
            nameof(CampMember), member.Id,
            granted
                ? $"Granted Early Entry to member in season {member.CampSeason.Year}."
                : $"Revoked Early Entry from member in season {member.CampSeason.Year}.",
            actorUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        return SetEarlyEntryOutcome.Success;
    }

}
