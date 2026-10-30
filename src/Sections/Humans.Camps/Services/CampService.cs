using System.Globalization;
using System.Transactions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.CityPlanning.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Base.Enums;
using NodaTime;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

using Humans.Teams.Contracts;

namespace Humans.Camps.Services;

internal sealed class CampService : ICampManagement, ICampLeadDirectory, IUserMerge
{
    /// <summary>GDPR export JSON key for this contributor's data.</summary>
    internal const string CampRoleAssignments = "CampRoleAssignments";
    internal const string CampMemberships = "CampMemberships";

    private readonly ICampRepository _repo;
    private readonly IAuditLogService _auditLog;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly IFileStorage _fileStorage;
    private readonly CampImageGallery _imageGallery;
    private readonly CampMembershipNotices _membershipNotices;
    private readonly Lazy<ICampRoleService> _campRoleService;
    // Lazy: CityPlanningService injects ICampService, closing the cycle.
    private readonly Lazy<ICityPlanningWrite> _cityPlanningService;
    private readonly IUserService _userService;
    private readonly ITicketService _tickets;
    private readonly ISettingsService _settingsService;
    private readonly IClock _clock;
    private readonly ILogger<CampService> _logger;

    private const int MaxHistoricalNameLength = 256;
    private const int MaxCampSlugLength = 256;

    public CampService(
        ICampRepository repo,
        IAuditLogService auditLog,
        ISystemTeamSync systemTeamSync,
        IFileStorage fileStorage,
        INotificationEmitter notificationEmitter,
        Lazy<ICampRoleService> campRoleService,
        Lazy<ICityPlanningWrite> cityPlanningService,
        IUserService userService,
        ITicketService tickets,
        ISettingsService settingsService,
        IClock clock,
        ILogger<CampService> logger)
    {
        _repo = repo;
        _auditLog = auditLog;
        _systemTeamSync = systemTeamSync;
        _fileStorage = fileStorage;
        _imageGallery = new CampImageGallery(repo, fileStorage, clock, auditLog, logger);
        _membershipNotices = new CampMembershipNotices(repo, userService, notificationEmitter, logger);
        _campRoleService = campRoleService;
        _cityPlanningService = cityPlanningService;
        _userService = userService;
        _tickets = tickets;
        _settingsService = settingsService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CampWriteResult<Camp>> CreateCampAsync(
        Guid createdByUserId, string name, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        CampSeasonData seasonData, List<string>? historicalNames, int year,
        CancellationToken cancellationToken = default)
    {
        if (historicalNames?.Any(string.IsNullOrWhiteSpace) == true)
            return new(null, "Camps_Flash_NameRequired");
        if (historicalNames?.Any(n => n.Trim().Length > MaxHistoricalNameLength) == true)
            return new(null, "Camps_Validation_HistoricalNameLength");

        var slug = SlugHelper.GenerateSlug(name);
        if (slug.Length == 0)
            slug = "camp";
        if (SlugHelper.IsReservedCampSlug(slug))
        {
            return new(null, "Camps_Flash_ReservedName");
        }

        var baseSlug = slug;
        var suffix = 2;
        while (await _repo.SlugExistsAsync(slug, cancellationToken))
        {
            var suffixText = "-" + suffix.ToString(CultureInfo.InvariantCulture);
            var prefixLength = Math.Min(baseSlug.Length, MaxCampSlugLength - suffixText.Length);
            slug = baseSlug[..prefixLength].TrimEnd('-') + suffixText;
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
                Name = oldName.Trim(),
                Source = CampNameSource.Manual,
                CreatedAt = now
            }).ToList();
        }

        await _repo.CreateCampAsync(camp, season, member, leadAssignment, historicalNameEntities, cancellationToken);

        await _auditLog.LogAsync(
            CampsAuditActions.CampCreated, nameof(Camp), camp.Id,
            $"Registered camp '{name}' for {year}",
            createdByUserId);

        await _systemTeamSync.SyncMembershipForUserAsync(
            createdByUserId, SystemTeamType.BarrioLeads, cancellationToken);

        return new(camp);
    }

    public async Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetBySlugAsync(slug, cancellationToken);
        // GetBySlugAsync does not load Seasons.Members, so EE/member counts are
        // unknown here — emit null rather than a misleading 0.
        if (camp is null) return null;
        var roles = await GetRoleProjectionForYearsAsync(
            camp.Seasons.Select(season => season.Year).Distinct().ToList(),
            cancellationToken);
        return CreateCampInfo(camp, includeEarlyEntryGrantCount: false, roles);
    }

    public async Task<CampInfo?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        // GetByIdAsync loads Seasons.Members, so the EE grant count projects correctly here
        // (unlike the by-slug read above).
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null) return null;
        var roles = await GetRoleProjectionForYearsAsync(
            camp.Seasons.Select(season => season.Year).Distinct().ToList(),
            cancellationToken);
        return CreateCampInfo(camp, roles: roles);
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
        var roles = await GetRoleProjectionForYearsAsync([year], cancellationToken);
        return camps.Select(c => CreateCampInfo(c, roles: roles)).ToList();
    }

    public async Task<CampUserInfo> GetCampUserInfoAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var camps = await GetCampsForYearAsync(settings.PublicYear, cancellationToken);
        return CampUserInfo.Resolve(camps, settings.PublicYear, userId);
    }

    public async Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(cancellationToken);
        if (settings is null)
            throw new InvalidOperationException("Camp settings not found.");

        var info = new CampSettingsInfo(
            await GetActiveYearAsync(cancellationToken),
            settings.OpenSeasons.ToList());
        if (info.OpenSeasons.Count == 0)
        {
            return info;
        }

        var nameLockDates = await _repo.GetNameLockDatesAsync(info.OpenSeasons, cancellationToken);
        return info with { NameLockDates = nameLockDates.ToDictionary(kv => kv.Key, kv => kv.Value) };
    }

    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var activeEvent = await _settingsService.GetActiveEventSettingsAsync(ct);
        if (activeEvent?.EarlyEntryStartOffset is not { } offset)
        {
            return [];
        }

        var eeStartDate = activeEvent.GateOpeningDate.PlusDays(offset);
        var year = activeEvent.Year;
        var camps = await GetCampsForYearAsync(year, ct);
        return camps
            .SelectMany(camp => camp.Seasons.Where(season => season.Year == year))
            .SelectMany(season => season.ActiveMembers
                .Where(member => member.HasEarlyEntry)
                .Select(member => new EarlyEntryGrant(
                    member.UserId,
                    eeStartDate,
                    $"Camp: {season.Name}")))
            .ToList();
    }

    /// <summary>The active event's year, falling back to the clock's current year before an event exists.</summary>
    public async Task<int> GetActiveYearAsync(CancellationToken cancellationToken = default)
    {
        var activeEvent = await _settingsService.GetActiveEventSettingsAsync(cancellationToken);
        return activeEvent?.Year > 0 ? activeEvent.Year : _clock.GetCurrentInstant().InUtc().Year;
    }

    // Served from the cached snapshot in CachingCampService: search must never hit the DB, so reaching the inner
    // service means a DI mistake.
    public Task<IReadOnlyList<CampSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Camp search runs against the cached CampInfo snapshot in CachingCampService. " +
            "If this is being called on the inner CampService it indicates a DI registration " +
            "mistake — ICampService must resolve to the caching decorator.");

    /// <summary>
    /// Special-role user-id lists (Lead/Workshop) keyed by season+role, plus the full
    /// named-role list per camp member — both projected from a single active-assignment
    /// fetch (active definitions, active members).
    /// </summary>
    private sealed record CampRoleProjection(
        IReadOnlyDictionary<(Guid CampSeasonId, CampSpecialRole Role), IReadOnlyList<Guid>> SpecialRoleUserIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> MemberRoleNames)
    {
        public static readonly CampRoleProjection Empty = new(
            new Dictionary<(Guid, CampSpecialRole), IReadOnlyList<Guid>>(),
            new Dictionary<Guid, IReadOnlyList<string>>());
    }

    private async Task<CampRoleProjection> GetRoleProjectionForYearsAsync(
        IReadOnlyCollection<int> years, CancellationToken cancellationToken)
    {
        if (years.Count == 0) return CampRoleProjection.Empty;

        var assignments = await _repo.GetActiveAssignmentsForYearsAsync(years, cancellationToken);

        var specialRoleUserIds = assignments
            .Where(a => a.Definition.SpecialRole is CampSpecialRole.Lead or CampSpecialRole.Workshop)
            .GroupBy(a => (a.CampSeasonId, a.Definition.SpecialRole))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group
                    .Select(a => a.CampMember.UserId)
                    .Distinct()
                    .ToList());

        var memberRoleNames = assignments
            .GroupBy(a => a.CampMemberId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .OrderBy(a => a.Definition.SortOrder)
                    .ThenBy(a => a.Definition.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(a => a.Definition.Name)
                    .ToList());

        return new CampRoleProjection(specialRoleUserIds, memberRoleNames);
    }

    private static CampInfo CreateCampInfo(
        Camp camp,
        bool includeEarlyEntryGrantCount = true,
        CampRoleProjection? roles = null)
    {
        return new CampInfo(
            camp.Id,
            camp.Slug,
            camp.ContactEmail,
            camp.ContactPhone,
            camp.IsSwissCamp,
            camp.TimesAtNowhere,
            camp.Seasons
                .Select(s => CreateCampSeasonInfo(s, camp.Slug, includeEarlyEntryGrantCount, roles))
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
        CampRoleProjection? roles = null)
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
                .Select(m => new CampSeasonMemberInfo(
                    m.Id,
                    m.UserId,
                    m.Status,
                    m.RequestedAt,
                    m.ConfirmedAt,
                    m.HasEarlyEntry)
                {
                    Roles = roles?.MemberRoleNames.TryGetValue(m.Id, out var names) == true
                        ? names
                        : []
                })
                .ToList(),
            LeadUserIds = GetSpecialRoleUserIds(season.Id, CampSpecialRole.Lead, roles?.SpecialRoleUserIds),
            WorkshopLeadUserIds = GetSpecialRoleUserIds(season.Id, CampSpecialRole.Workshop, roles?.SpecialRoleUserIds)
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

    public async Task<CampWriteResult<CampSeason>> OptInToSeasonAsync(
        Guid campId, int year, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.OpenSeasons.Contains(year))
        {
            return new(null, "Camps_Flash_SeasonNotOpen");
        }

        if (await _repo.SeasonExistsAsync(campId, year, cancellationToken))
        {
            return new(null, "Camps_Flash_SeasonAlreadyExists");
        }

        var previousSeason = await _repo.GetLatestSeasonAsync(campId, cancellationToken);
        if (previousSeason is null) return new(null, "Camps_Flash_NoPreviousSeason");

        var hasApprovedSeason = await _repo.HasApprovedSeasonAsync(campId, cancellationToken);

        var now = _clock.GetCurrentInstant();
        var newSeason = hasApprovedSeason
            ? previousSeason.CreateApprovedRenewal(Guid.NewGuid(), year, now)
            : previousSeason.CreatePendingRenewal(Guid.NewGuid(), year, now);

        await _repo.AddSeasonAsync(newSeason, cancellationToken);

        var description = $"Opted in to season {year} (auto-approved: {hasApprovedSeason})";
        if (actorUserId is { } actorId)
        {
            await _auditLog.LogAsync(
                CampsAuditActions.CampSeasonCreated, nameof(CampSeason), newSeason.Id,
                description, actorId,
                relatedEntityId: campId, relatedEntityType: nameof(Camp));
        }
        else
        {
            // Dev-persona provisioning runs before sign-in and has no human actor.
            await _auditLog.LogAsync(
                CampsAuditActions.CampSeasonCreated, nameof(CampSeason), newSeason.Id,
                description, nameof(CampService),
                relatedEntityId: campId, relatedEntityType: nameof(Camp));
        }

        return new(newSeason);
    }

    public async Task<CampUpdateResult> UpdateSeasonAsync(
        Guid scopedCampId, Guid seasonId, CampSeasonData data, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusalKey = null;
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.CampId != scopedCampId)
            {
                refusalKey = "Camps_Flash_SeasonWrongCamp";
                return;
            }

            ApplySeasonDetails(season, data);
            season.UpdatedAt = now;

            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (refusalKey is not null) return CampUpdateResult.Failure(refusalKey);
        if (!found)
        {
            return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");
        }

        await _auditLog.LogAsync(
            CampsAuditActions.CampUpdated, nameof(CampSeason), seasonId,
            $"Updated season {year} details",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        return CampUpdateResult.Success();

    }

    public async Task<CampAdminMutationResult> ApproveSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default)
    {
        if (notes is { Length: > 2000 })
        {
            return new(false, "Review notes must be 2000 characters or fewer.");
        }

        string? refusal = null;
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status != CampSeasonStatus.Pending)
            {
                refusal = $"Cannot approve a season with status {season.Status}.";
                return;
            }
            season.Approve(reviewedByUserId, notes, now);
            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (refusal is not null) return new(false, refusal);
        if (!found) return new(false, "Season not found.");

        await _auditLog.LogAsync(
            CampsAuditActions.CampSeasonApproved, nameof(CampSeason), seasonId,
            $"Approved season {year}",
            reviewedByUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
        return new(true);

    }

    public async Task<CampAdminMutationResult> RejectSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default)
    {
        if (notes is { Length: > 2000 })
        {
            return new(false, "Review notes must be 2000 characters or fewer.");
        }

        string? refusal = null;
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status != CampSeasonStatus.Pending)
            {
                refusal = $"Cannot reject a season with status {season.Status}.";
                return;
            }
            season.Reject(reviewedByUserId, notes, now);
            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (refusal is not null) return new(false, refusal);
        if (!found) return new(false, "Season not found.");

        await _auditLog.LogAsync(
            CampsAuditActions.CampSeasonRejected, nameof(CampSeason), seasonId,
            $"Rejected season {year}: {notes}",
            reviewedByUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await _membershipNotices.NotifyPendingRequestersOfSeasonClosureAsync(seasonId, campId, year);
        return new(true);

    }

    public async Task<CampUpdateResult> WithdrawSeasonAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusalKey = null;
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.CampId != scopedCampId)
            {
                refusalKey = "Camps_Flash_SeasonWrongCamp";
                return;
            }

            if (season.Status is not (CampSeasonStatus.Pending or CampSeasonStatus.Active))
            {
                refusalKey = "Camps_Flash_SeasonWithdrawRequiresOpen";
                return;
            }
            season.Withdraw(now);
            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (refusalKey is not null) return CampUpdateResult.Failure(refusalKey);
        if (!found)
        {
            return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");
        }

        await _auditLog.LogAsync(
            CampsAuditActions.CampSeasonWithdrawn, nameof(CampSeason), seasonId,
            $"Withdrew from season {year}",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await _membershipNotices.NotifyPendingRequestersOfSeasonClosureAsync(seasonId, campId, year);

        return CampUpdateResult.Success();

    }

    public async Task<CampUpdateResult> MarkSeasonFullAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusalKey = null;
        var now = _clock.GetCurrentInstant();
        var year = 0;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.CampId != scopedCampId)
            {
                refusalKey = "Camps_Flash_SeasonWrongCamp";
                return;
            }

            if (season.Status != CampSeasonStatus.Active)
            {
                refusalKey = "Camps_Flash_SeasonFullRequiresActive";
                return;
            }

            season.MarkFull(now);
            year = season.Year;
        }, cancellationToken);

        if (refusalKey is not null) return CampUpdateResult.Failure(refusalKey);
        if (!found)
        {
            return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");
        }

        await _auditLog.LogAsync(
            CampsAuditActions.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {year} status set to Full",
            actorUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));

        return CampUpdateResult.Success();

    }

    public async Task<CampAdminMutationResult> ReactivateSeasonAsync(Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusal = null;
        var now = _clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;
        var previousStatus = CampSeasonStatus.Pending;
        var newStatus = CampSeasonStatus.Pending;

        var found = await _repo.UpdateSeasonAsync(seasonId, season =>
        {
            if (season.Status is not (CampSeasonStatus.Full or CampSeasonStatus.Withdrawn))
            {
                refusal = $"Cannot reactivate a season with status {season.Status}.";
                return;
            }
            previousStatus = season.Status;
            newStatus = season.Reactivate(now);
            year = season.Year;
            campId = season.CampId;
        }, cancellationToken);

        if (refusal is not null) return new(false, refusal);
        if (!found) return new(false, "Season not found.");

        await _auditLog.LogAsync(
            CampsAuditActions.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {year} status changed from {previousStatus} to {newStatus}",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
        return new(true);

    }

    public async Task<CampUpdateResult> UpdateCampAsync(
        CampUpdateInput input,
        Guid actorUserId, CancellationToken cancellationToken = default)
    {
        // Prove the season belongs to the camp before the first write: the scoped check
        // inside UpdateSeasonAsync fires only after the camp-level fields have committed,
        // which would leave a partial update behind an uninvalidated cache on failure.
        var scopedSeason = await _repo.GetSeasonByIdAsync(input.SeasonId, cancellationToken);
        if (scopedSeason is null)
        {
            return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");
        }

        if (scopedSeason.CampId != input.CampId)
        {
            return CampUpdateResult.Failure("Camps_Flash_SeasonWrongCamp");
        }

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
            return CampUpdateResult.Failure("Camps_Flash_CampNotFound");
        }

        await _auditLog.LogAsync(
            CampsAuditActions.CampUpdated, nameof(Camp), input.CampId,
            $"Updated camp {input.CampId}",
            actorUserId);

        var seasonUpdate = await UpdateSeasonAsync(input.CampId, input.SeasonId, input.SeasonData, actorUserId, cancellationToken);
        if (!seasonUpdate.Succeeded) return seasonUpdate;

        var currentSeason = await _repo.GetSeasonByIdAsync(input.SeasonId, cancellationToken);
        if (currentSeason is null) return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");

        if (!string.Equals(currentSeason.Name, input.SeasonName, StringComparison.Ordinal))
        {
            var today = _clock.GetCurrentInstant().InUtc().Date;
            var nameLocked = currentSeason.NameLockDate.HasValue && today >= currentSeason.NameLockDate.Value;
            if (!nameLocked)
            {
                var nameUpdate = await ChangeSeasonNameAsync(input.CampId, currentSeason.Id, input.SeasonName, actorUserId, cancellationToken);
                if (!nameUpdate.Succeeded) return nameUpdate;
            }
        }

        return CampUpdateResult.Success();
    }

    public async Task<CampAdminMutationResult> DeleteCampAsync(Guid campId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null) return new(false, "Camp not found.");

        // CityPlanning keys polygon history on CampSeasonId; clear it through its service before deleting. Both writes
        // share one ambient transaction so a failure in either can't leave a live camp with its polygon history gone.
        // A SaveCampPolygonAsync landing between cleanup and commit still orphans a row: an accepted trade.
        var seasonIds = camp.Seasons.Select(s => s.Id).ToList();
        IReadOnlyList<string>? deletedImagePaths;

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            if (seasonIds.Count > 0)
            {
                var removed = await _cityPlanningService.Value
                    .DeleteCampPolygonsForSeasonsAsync(seasonIds, cancellationToken);
                if (removed > 0)
                {
                    _logger.LogInformation(
                        "Deleted {Rows} city-planning polygon/history rows for {Seasons} seasons of camp {CampId}",
                        removed, seasonIds.Count, campId);
                }
            }

            deletedImagePaths = await _repo.DeleteCampAsync(campId, cancellationToken);
            if (deletedImagePaths is null)
            {
                return new(false, "Camp not found.");
            }

            scope.Complete();
        }

        await _auditLog.LogAsync(
            CampsAuditActions.CampDeleted, nameof(Camp), campId,
            $"Camp {campId} permanently deleted",
            actorUserId);

        // Metadata and audit have committed; file cleanup must finish independently of the request.
        foreach (var path in deletedImagePaths)
        {
            try
            {
                await _fileStorage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete camp image file at {StoragePath} during camp delete for {CampId}; DB row already removed",
                    path, campId);
            }
        }
        return new(true);

    }

    public async Task<CampUpdateResult> AddHistoricalNameAsync(
        Guid campId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CampUpdateResult.Failure("Camps_Flash_NameRequired");
        name = name.Trim();
        if (name.Length > MaxHistoricalNameLength)
            return CampUpdateResult.Failure("Camps_Validation_HistoricalNameLength");

        var entry = new CampHistoricalName
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Name = name,
            Source = CampNameSource.Manual,
            CreatedAt = _clock.GetCurrentInstant()
        };

        await _repo.AddHistoricalNameAsync(entry, cancellationToken);
        return CampUpdateResult.Success();
    }

    public async Task<CampUpdateResult> RemoveHistoricalNameAsync(
        Guid scopedCampId, Guid historicalNameId, CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetByIdAsync(scopedCampId, cancellationToken);
        if (camp is null) return CampUpdateResult.Failure("Camps_Flash_CampNotFound");
        if (camp.HistoricalNames.All(n => n.Id != historicalNameId))
        {
            return CampUpdateResult.Failure("Camps_Flash_HistoricalNameWrongCamp");
        }

        var removed = await _repo.RemoveHistoricalNameAsync(historicalNameId, cancellationToken);
        if (!removed)
        {
            return CampUpdateResult.Failure("Camps_Flash_HistoricalNameNotFound");
        }
        return CampUpdateResult.Success();

    }

    public async Task<CampSeasonInfo?> GetCampSeasonByIdAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var season = await _repo.GetSeasonByIdAsync(campSeasonId, cancellationToken);
        if (season is null) return null;
        var roles = await GetRoleProjectionForYearsAsync([season.Year], cancellationToken);
        return CreateCampSeasonInfo(
            season,
            season.Camp?.Slug ?? string.Empty,
            roles: roles);
    }

    public async Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken cancellationToken = default)
    {
        var row = await _repo.GetMemberLookupAsync(campMemberId, cancellationToken);
        return row is null ? null : new CampMemberLookup(row.Value.CampSeasonId, row.Value.UserId, row.Value.Status);
    }

    public Task<CampImageUploadResult> UploadImageAsync(
        Guid campId, Stream fileStream, string fileName, string contentType, long length,
        Guid actorUserId, CancellationToken cancellationToken = default) =>
        _imageGallery.UploadImageAsync(campId, fileStream, fileName, contentType, length, actorUserId, cancellationToken);

    public Task<CampUpdateResult> DeleteImageAsync(
        Guid scopedCampId, Guid imageId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _imageGallery.DeleteImageAsync(scopedCampId, imageId, actorUserId, cancellationToken);

    public Task ReorderImagesAsync(
        Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default) =>
        _imageGallery.ReorderImagesAsync(campId, imageIdsInOrder, cancellationToken);

    public async Task OpenSeasonAsync(int year, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(cancellationToken)
            ?? throw new InvalidOperationException("Camp settings not found.");
        // Complete the save and audit together even if the caller stops waiting.
        if (await _repo.OpenSeasonAsync(year, CancellationToken.None))
        {
            await _auditLog.LogAsync(CampsAuditActions.CampRegistrationOpened, nameof(CampSettings), settings.Id,
                $"Opened camp registration for {year}", actorUserId);
        }
    }

    public async Task CloseSeasonAsync(int year, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(cancellationToken)
            ?? throw new InvalidOperationException("Camp settings not found.");
        // Complete the save and audit together even if the caller stops waiting.
        if (await _repo.CloseSeasonAsync(year, CancellationToken.None))
        {
            await _auditLog.LogAsync(CampsAuditActions.CampRegistrationClosed, nameof(CampSettings), settings.Id,
                $"Closed camp registration for {year}", actorUserId);
        }
    }

    public async Task SetNameLockDateAsync(
        int year, LocalDate lockDate, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(cancellationToken)
            ?? throw new InvalidOperationException("Camp settings not found.");
        // Complete the save and audit together even if the caller stops waiting.
        var changed = await _repo.SetNameLockDateForYearAsync(year, lockDate, CancellationToken.None);
        if (changed > 0)
        {
            await _auditLog.LogAsync(CampsAuditActions.CampNameLockDateChanged, nameof(CampSettings), settings.Id,
                $"Set name lock date for {year} to {lockDate.ToInvariantDate()} on {changed} camp seasons", actorUserId);
        }
    }

    public async Task<CampUpdateResult> ChangeSeasonNameAsync(
        Guid scopedCampId, Guid seasonId, string newName, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusalKey = null;
        var now = _clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        string? oldName = null;
        var campId = Guid.Empty;

        var found = await _repo.ApplyNameChangeAsync(seasonId, season =>
        {
            if (season.CampId != scopedCampId)
            {
                refusalKey = "Camps_Flash_SeasonWrongCamp";
                return null;
            }

            if (season.NameLockDate.HasValue && today >= season.NameLockDate.Value)
            {
                refusalKey = "Camps_Edit_NameLocked";
                return null;
            }

            if (string.Equals(season.Name, newName, StringComparison.Ordinal))
            {
                return null;
            }

            oldName = season.Name;
            campId = season.CampId;

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

        if (refusalKey is not null) return CampUpdateResult.Failure(refusalKey);
        if (!found)
        {
            return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");
        }

        if (oldName is null)
        {
            return CampUpdateResult.Success();
        }

        await _auditLog.LogAsync(
            CampsAuditActions.CampNameChanged, nameof(CampSeason), seasonId,
            $"Name changed from '{oldName}' to '{newName}'",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        return CampUpdateResult.Success();

    }

    private static CampSeason CreateSeasonFromData(
        Guid campId, int year, string name, CampSeasonData data, Instant now)
    {
        var season = new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            Year = year,
            Name = name,
            Status = CampSeasonStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplySeasonDetails(season, data);
        return season;
    }

    private static void ApplySeasonDetails(CampSeason season, CampSeasonData data)
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
        season.Vibes = [.. data.Vibes];
        season.AdultPlayspace = data.AdultPlayspace;
        season.MemberCount = data.MemberCount;
        season.SpaceRequirement = data.SpaceRequirement;
        season.SoundZone = data.SoundZone;
        season.ElectricalGrid = data.ElectricalGrid;
    }

    /// <summary>Sole CampMember→Removed transition: role-cascade, state flip, audit. Callers own preconditions and post-effects.</summary>
    private async Task TransitionMemberToRemovedAsync(
        CampMember member,
        Guid actorUserId,
        AuditActionDefinition auditAction,
        string auditMessage,
        bool cascadeRoleAssignments,
        CancellationToken cancellationToken)
    {
        if (cascadeRoleAssignments)
        {
            await _campRoleService.Value.RemoveAllForMemberAsync(
                member.Id, actorUserId, cancellationToken);
        }

        // A grant whose holder already entered the event is consumed: keep the
        // flag on the Removed row so the slot-cap count still includes it —
        // otherwise removing the member frees the slot for a second entry.
        var retainConsumedEeGrant = member.HasEarlyEntry
            && await HasEnteredEventAsync(member.UserId, member.CampSeason.Year, cancellationToken);

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Removed;
        member.RemovedAt = now;
        member.RemovedByUserId = actorUserId;
        member.HasEarlyEntry = retainConsumedEeGrant;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            auditAction, nameof(CampMember), member.Id,
            auditMessage, actorUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));
    }

    public async Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        // Active AND Full both accept requests — Full is an informational label the lead
        // sets to say "we look full," not an enforcement gate. Humans doesn't yet know
        // everyone actually in the camp, so people still need to be able to request/join.
        var season = camp?.Seasons.FirstOrDefault(s =>
            s.Year == settings.PublicYear
            && (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full));
        if (season is null)
        {
            return new CampMemberRequestResult(
                Guid.Empty,
                CampMemberRequestOutcome.NoOpenSeason,
                "Camps_Flash_RequestNoOpenSeason",
                CampMemberRequestNoticeLevel.Error);
        }

        var now = _clock.GetCurrentInstant();
        var insert = await _repo.RequestMembershipAsync(season.Id, userId, now, cancellationToken);

        if (insert.Outcome == CampMemberInsertOutcome.Created)
        {
            await _auditLog.LogAsync(
                CampsAuditActions.CampMemberRequested, nameof(CampMember), insert.MemberId,
                $"Requested membership in camp season {season.Year}",
                userId,
                relatedEntityId: campId, relatedEntityType: nameof(Camp));
        }

        return insert.Outcome switch
        {
            CampMemberInsertOutcome.Created =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.Created,
                    "Camps_Flash_RequestCreated",
                    CampMemberRequestNoticeLevel.Success),
            CampMemberInsertOutcome.AlreadyActive =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.AlreadyActive,
                    "Camps_Flash_RequestAlreadyActive",
                    CampMemberRequestNoticeLevel.Info),
            _ =>
                new CampMemberRequestResult(
                    insert.MemberId,
                    CampMemberRequestOutcome.AlreadyPending,
                    "Camps_Flash_RequestAlreadyPending",
                    CampMemberRequestNoticeLevel.Info)
        };
    }

    public async Task<CampMembershipMutationResult> ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Pending)
        {
            return CampMembershipMutationResult.Failure("Camps_Flash_ApproveRequiresPending");
        }

        var now = _clock.GetCurrentInstant();
        member.Status = CampMemberStatus.Active;
        member.ConfirmedAt = now;
        member.ConfirmedByUserId = approvedByUserId;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            CampsAuditActions.CampMemberApproved, nameof(CampMember), member.Id,
            $"Approved camp membership for season {member.CampSeason.Year}",
            approvedByUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));

        await _membershipNotices.NotifyApprovalAsync(scopedCampId, member.CampSeasonId, member.UserId, member.Id);
        return CampMembershipMutationResult.Success();

    }

    public async Task<CampMembershipMutationResult> RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Pending)
            return CampMembershipMutationResult.Failure("Camps_Flash_RejectRequiresPending");

        var requesterUserId = member.UserId;
        var seasonId = member.CampSeasonId;

        await TransitionMemberToRemovedAsync(
            member, rejectedByUserId,
            CampsAuditActions.CampMemberRejected,
            $"Rejected camp membership request for season {member.CampSeason.Year}",
            cascadeRoleAssignments: false,
            cancellationToken);

        await _membershipNotices.NotifyRejectionAsync(scopedCampId, seasonId, requesterUserId, member.Id);
        return CampMembershipMutationResult.Success();

    }

    public async Task<CampMembershipMutationResult> RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Active)
            return CampMembershipMutationResult.Failure("Camps_Flash_RemoveRequiresActive");

        await TransitionMemberToRemovedAsync(
            member, removedByUserId,
            CampsAuditActions.CampMemberRemoved,
            $"Removed camp member from season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);
        return CampMembershipMutationResult.Success();

    }

    private async Task<Guid?> EnsureActiveCampMemberAsync(Guid campSeasonId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetUserInfoAsync(userId, cancellationToken);
        if (user is null || user.IsTombstone)
            return null;
        // Merge aliases identify the survivor; never create a new footprint on the archived account.
        userId = user.Id;
        var now = _clock.GetCurrentInstant();
        var result = await _repo.AddActiveMembershipAsync(campSeasonId, userId, now, actorUserId, cancellationToken);

        if (result.Outcome != CampMemberInsertOutcome.AlreadyActive)
        {
            await _auditLog.LogAsync(
                CampsAuditActions.CampMemberAddedByLead,
                nameof(CampMember), result.MemberId,
                "Lead added human as active camp member.",
                actorUserId,
                relatedEntityId: userId, relatedEntityType: nameof(User));
        }

        return result.MemberId;
    }

    public async Task<AddCampMemberOutcome> AddCampMemberToActiveSeasonAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, int? year = null)
    {
        if (userId == Guid.Empty)
            return AddCampMemberOutcome.InvalidUser;

        var targetYear = year ?? await GetActiveYearAsync(cancellationToken);
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        // Full is informational only: it must not block camp management.
        var openSeason = camp?.Seasons.FirstOrDefault(s => s.Year == targetYear
            && s.Status is CampSeasonStatus.Active or CampSeasonStatus.Full);
        if (openSeason is null)
            return AddCampMemberOutcome.NoActiveSeason;

        var memberId = await EnsureActiveCampMemberAsync(openSeason.Id, userId, actorUserId, cancellationToken);
        return memberId is null ? AddCampMemberOutcome.InvalidUser : AddCampMemberOutcome.Added;
    }

    public async Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, int? year = null)
    {
        var targetYear = year ?? await GetActiveYearAsync(cancellationToken);
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        // Full is informational only: it must not block camp management.
        var openSeason = camp?.Seasons.FirstOrDefault(s => s.Year == targetYear
            && s.Status is CampSeasonStatus.Active or CampSeasonStatus.Full);
        if (openSeason is null)
            return AssignCampRoleOutcome.SeasonNotFound;

        var memberId = await EnsureActiveCampMemberAsync(openSeason.Id, userId, actorUserId, cancellationToken);
        if (memberId is null)
            return AssignCampRoleOutcome.MemberNotFound;
        return await _campRoleService.Value.AssignAsync(
            openSeason.Id, roleDefinitionId, memberId.Value, actorUserId, cancellationToken);
    }

    public async Task<CampMembershipMutationResult> WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken);
        if (member is null) return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");

        if (member.Status != CampMemberStatus.Pending)
            return CampMembershipMutationResult.Failure("Camps_Flash_WithdrawRequiresPending");

        await TransitionMemberToRemovedAsync(
            member, userId,
            CampsAuditActions.CampMemberWithdrawn,
            $"Withdrew camp membership request for season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);

        return CampMembershipMutationResult.Success();

    }

    public async Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken);
        if (member is null)
        {
            return CampMembershipMutationResult.Failure("Camps_Flash_RoleMemberNotFound");
        }

        if (member.Status != CampMemberStatus.Active)
        {
            return CampMembershipMutationResult.Failure("Camps_Flash_LeaveRequiresActive");
        }

        await TransitionMemberToRemovedAsync(
            member, userId,
            CampsAuditActions.CampMemberLeft,
            $"Left camp season {member.CampSeason.Year}",
            cascadeRoleAssignments: true,
            cancellationToken);

        return CampMembershipMutationResult.Success();
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        // Called from AccountMergeService.MergeAsync's ordered fan-out; must stay idempotent.
        await _repo.ReassignMembershipsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

        // Lead moves change Barrio Leads team membership for both users.
        await _systemTeamSync.SyncMembershipForUserAsync(sourceUserId, SystemTeamType.BarrioLeads, ct);
        await _systemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.BarrioLeads, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        userId = (await _userService.GetUserInfoAsync(userId, ct))?.Id ?? userId;
        var roleAssignments = await _repo.GetAllAssignmentsForUserAsync(userId, ct);

        var shapedRoles = roleAssignments.Select(a => new
        {
            CampSlug = a.CampSeason.Camp.Slug,
            SeasonYear = a.CampSeason.Year,
            RoleName = a.Definition.Name,
            AssignedAt = a.AssignedAt.ToIso8601(),
            a.AssignedByUserId
        }).ToList();

        var memberships = await _repo.GetAllMembershipsForUserAsync(userId, ct);
        var shapedMemberships = memberships.Select(m => new
        {
            m.Id,
            m.CampSeasonId,
            CampSlug = m.CampSeason.Camp.Slug,
            SeasonYear = m.CampSeason.Year,
            Status = m.Status.ToString(),
            RequestedAt = m.RequestedAt.ToIso8601(),
            ConfirmedAt = m.ConfirmedAt?.ToIso8601(),
            m.ConfirmedByUserId,
            RemovedAt = m.RemovedAt?.ToIso8601(),
            m.RemovedByUserId,
            m.HasEarlyEntry
        }).ToList();

        return [new UserDataSlice(CampRoleAssignments, shapedRoles), new UserDataSlice(CampMemberships, shapedMemberships)];
    }

    internal static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CampRoleAssignments] = null,
            [CampMemberships] = null
        };

    /// <summary>
    /// Drops the user's camp memberships and every role assignment hanging off
    /// them (Camp Lead included — it is a role assignment).
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await _repo.DeleteCampFootprintForUserAsync(userId, ct);
        // The cached CampInfo rosters this just emptied are dropped by CachingCampService,
        // the registered IUserDataContributor, which refreshes after this returns.
    }

    public async Task<CampAdminMutationResult> SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (slotCount < 0)
            return new(false, "EE slot count cannot be negative.");

        var result = await _repo.SetCampSeasonEeSlotCountAsync(campSeasonId, slotCount, cancellationToken);
        if (result is null)
            return new(false, "Camp season not found.");

        var (oldValue, newValue, campId) = result.Value;
        if (oldValue == newValue) return new(true);

        await _auditLog.LogAsync(
            CampsAuditActions.CampSeasonEeSlotCountChanged,
            nameof(CampSeason), campSeasonId,
            $"EE slot count changed from {oldValue} to {newValue}.",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
        return new(true);
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
        else if (await HasEnteredEventAsync(member.UserId, member.CampSeason.Year, cancellationToken))
        {
            // The grant is consumed once the holder is through the gate —
            // revoking it would free the slot for someone else, yielding
            // N+1 early entries from N slots.
            return SetEarlyEntryOutcome.MemberAlreadyEntered;
        }

        member.HasEarlyEntry = granted;
        await _repo.SaveMemberAsync(member, cancellationToken);

        await _auditLog.LogAsync(
            granted ? CampsAuditActions.CampEarlyEntryGranted : CampsAuditActions.CampEarlyEntryRevoked,
            nameof(CampMember), member.Id,
            granted
                ? $"Granted Early Entry to member in season {member.CampSeason.Year}."
                : $"Revoked Early Entry from member in season {member.CampSeason.Year}.",
            actorUserId,
            relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp));

        return SetEarlyEntryOutcome.Success;
    }

    /// <summary>An Attended participation row for the season's year (gate check-in via ticket sync).</summary>
    private async ValueTask<bool> HasEnteredEventAsync(Guid userId, int year, CancellationToken ct) =>
        (await _tickets.GetParticipationsAsync(userId, ct)).Any(p =>
            p.Year == year && p.Status == ParticipationStatus.Attended);

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken cancellationToken = default) =>
        _repo.GetActiveLeadUserIdsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _repo.IsLeadAnywhereAsync(userId, cancellationToken);
}
