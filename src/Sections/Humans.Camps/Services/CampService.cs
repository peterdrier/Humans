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
    private readonly CampReadProjection _readProjection;
    private readonly CampMembershipNotices _membershipNotices;
    private readonly CampMembershipTransitions _memberships;
    // Lazy: CityPlanningService injects ICampService, closing the cycle.
    private readonly Lazy<ICityPlanningWrite> _cityPlanningService;
    private readonly IUserService _userService;
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
        _readProjection = new CampReadProjection(repo);
        _auditLog = auditLog;
        _systemTeamSync = systemTeamSync;
        _fileStorage = fileStorage;
        _imageGallery = new CampImageGallery(repo, fileStorage, clock, auditLog, logger);
        _membershipNotices = new CampMembershipNotices(repo, userService, notificationEmitter, logger);
        _memberships = new CampMembershipTransitions(repo, auditLog, userService, settingsService, tickets,
            campRoleService, _membershipNotices, clock);
        _cityPlanningService = cityPlanningService;
        _userService = userService;
        _settingsService = settingsService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CampWriteResult<Camp>> CreateCampAsync(
        Guid createdByUserId, string name, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        CampSeasonData seasonData, List<string>? historicalNames, Guid eventSettingsId,
        CancellationToken cancellationToken = default)
    {
        var targetEvent = (await GetRegistrationByEventAsync(cancellationToken))
            .FirstOrDefault(r => r.EventSettingsId == eventSettingsId);
        if (targetEvent is null)
            return new(null, "Camps_Flash_SeasonNotOpen");
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

        var season = CreateSeasonFromData(camp.Id, targetEvent.Year, name, seasonData, now);
        season.EventSettingsId = eventSettingsId;

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
            $"Registered camp '{name}' for {targetEvent.EventName}",
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
        var roles = await _readProjection.GetRoleProjectionForYearsAsync(
            camp.Seasons.Select(season => season.Year).Distinct().ToList(),
            cancellationToken);
        return CampReadProjection.CreateCampInfo(camp, includeEarlyEntryGrantCount: false, roles);
    }

    public async Task<CampInfo?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default)
    {
        // GetByIdAsync loads Seasons.Members, so the EE grant count projects correctly here
        // (unlike the by-slug read above).
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null) return null;
        var roles = await _readProjection.GetRoleProjectionForYearsAsync(
            camp.Seasons.Select(season => season.Year).Distinct().ToList(),
            cancellationToken);
        return CampReadProjection.CreateCampInfo(camp, roles: roles);
    }

    public async Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        Guid? eventSettingsId = null,
        CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetByIdAsync(campId, cancellationToken);
        if (camp is null)
        {
            return null;
        }

        var targetEventId = eventSettingsId
            ?? (await _settingsService.GetActiveEventSettingsAsync(cancellationToken))?.Id;
        var season = camp.Seasons
            .FirstOrDefault(s => targetEventId is not null && s.EventSettingsId == targetEventId)
            ?? camp.Seasons
                .OrderByDescending(s => s.Year)
                .ThenByDescending(s => s.CreatedAt)
                .FirstOrDefault();

        if (season is null)
        {
            return null;
        }

        return CampReadProjection.CreateCampEditData(
            camp,
            season,
            season.EventSettingsId,
            _clock.GetCurrentInstant().InUtc().Date);
    }

    public async Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var camps = await _repo.GetCampsWithLeadsForYearAsync(
            year, statusFilter: null, cancellationToken);
        var roles = await _readProjection.GetRoleProjectionForYearsAsync([year], cancellationToken);
        return camps.Select(c => CampReadProjection.CreateCampInfo(c, roles: roles)).ToList();
    }

    public async Task<IReadOnlyList<CampInfo>> GetCampsForEventAsync(
        Guid eventSettingsId, CancellationToken cancellationToken = default)
    {
        var targetEvent = (await GetRegistrationByEventAsync(cancellationToken))
            .FirstOrDefault(r => r.EventSettingsId == eventSettingsId);
        return targetEvent is null
            ? []
            : FilterToEvent(await GetCampsForYearAsync(targetEvent.Year, cancellationToken), eventSettingsId);
    }

    /// <summary>Camps with a season for the event, each carrying only that season.</summary>
    internal static IReadOnlyList<CampInfo> FilterToEvent(IEnumerable<CampInfo> camps, Guid eventSettingsId) =>
        [.. camps
            .Where(camp => camp.Seasons.Any(s => s.EventSettingsId == eventSettingsId))
            .Select(camp => camp with { Seasons = [.. camp.Seasons.Where(s => s.EventSettingsId == eventSettingsId)] })];

    public async Task<CampUserInfo> GetCampUserInfoAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (settings.ActiveEvent is not { } activeEvent)
        {
            return CampUserInfo.None;
        }

        var camps = await GetCampsForYearAsync(activeEvent.Year, cancellationToken);
        return CampUserInfo.Resolve(camps, activeEvent.EventSettingsId, userId);
    }

    public async Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        _ = await _repo.GetSettingsReadOnlyAsync(cancellationToken)
            ?? throw new InvalidOperationException("Camp settings not found.");

        var registration = await GetRegistrationByEventAsync(cancellationToken);
        var openYears = registration.Where(r => r.RegistrationOpen).Select(r => r.Year)
            .Distinct()
            .ToList();
        var (activeYear, activeEventId) = await GetActiveEventAsync(cancellationToken);
        var info = new CampSettingsInfo(activeYear, openYears)
        {
            ActiveEventSettingsId = activeEventId,
            Events = registration,
        };
        var openEvents = info.OpenEvents;
        if (openEvents.Count == 0)
        {
            return info;
        }

        var lockDates = await _repo.GetNameLockDatesAsync(
            openEvents.Select(e => e.Year).Distinct().ToList(), cancellationToken);
        var nameLockDates = new Dictionary<Guid, LocalDate?>();
        foreach (var openEvent in openEvents)
        {
            var ofEvent = lockDates
                .Where(r => r.EventSettingsId == openEvent.EventSettingsId)
                .ToList();
            if (ofEvent.Count > 0)
                nameLockDates[openEvent.EventSettingsId] = ofEvent.Max(r => r.LockDate);
        }

        return info with { NameLockDates = nameLockDates };
    }

    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var activeEvent = await _settingsService.GetActiveEventSettingsAsync(ct);
        if (activeEvent?.EarlyEntryStartOffset is not { } offset)
        {
            return [];
        }

        var eeStartDate = activeEvent.GateOpeningDate.PlusDays(offset);
        var camps = await GetCampsForEventAsync(activeEvent.Id, ct);
        return camps
            .SelectMany(camp => camp.Seasons)
            .SelectMany(season => season.ActiveMembers
                .Where(member => member.HasEarlyEntry)
                .Select(member => new EarlyEntryGrant(
                    member.UserId,
                    eeStartDate,
                    $"Camp: {season.Name}")))
            .ToList();
    }

    public async Task<(int Year, Guid? EventSettingsId)> GetActiveEventAsync(CancellationToken cancellationToken = default)
    {
        var activeEvent = await _settingsService.GetActiveEventSettingsAsync(cancellationToken);
        return (activeEvent?.Year > 0 ? activeEvent.Year : _clock.GetCurrentInstant().InUtc().Year, activeEvent?.Id);
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

    public async Task<CampWriteResult<CampSeason>> OptInToSeasonAsync(
        Guid campId, Guid eventSettingsId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var registration = await GetRegistrationByEventAsync(cancellationToken);
        var targetEvent = registration.FirstOrDefault(r => r.EventSettingsId == eventSettingsId);
        if (targetEvent is not { RegistrationOpen: true })
        {
            return new(null, "Camps_Flash_SeasonNotOpen");
        }

        if (await _repo.SeasonExistsAsync(
                campId, eventSettingsId, cancellationToken))
        {
            return new(null, "Camps_Flash_SeasonAlreadyExists");
        }

        var previousSeason = await _repo.GetLatestSeasonAsync(campId, cancellationToken);
        if (previousSeason is null) return new(null, "Camps_Flash_NoPreviousSeason");

        var hasApprovedSeason = await _repo.HasApprovedSeasonAsync(campId, cancellationToken);

        var now = _clock.GetCurrentInstant();
        var newSeason = hasApprovedSeason
            ? previousSeason.CreateApprovedRenewal(Guid.NewGuid(), targetEvent.Year, now)
            : previousSeason.CreatePendingRenewal(Guid.NewGuid(), targetEvent.Year, now);

        newSeason.EventSettingsId = eventSettingsId;
        await _repo.AddSeasonAsync(newSeason, cancellationToken);

        var description = $"Opted in to season {targetEvent.EventName} (auto-approved: {hasApprovedSeason})";
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

        // Metadata has committed and audit was attempted; cleanup must finish independently of the request.
        foreach (var path in deletedImagePaths)
        {
            try
            {
                await _fileStorage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to delete camp image file at {StoragePath} during camp delete for {CampId}; DB row already removed",
                    path, campId);
            }
        }
        return new(true);

    }

    public async Task<CampUpdateResult> AddHistoricalNameAsync(
        Guid campId, string name, Guid actorUserId, CancellationToken cancellationToken = default)
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
        await _auditLog.LogAsync(
            CampsAuditActions.CampHistoricalNameAdded, nameof(CampHistoricalName), entry.Id,
            $"Added historical name '{name}'", actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
        return CampUpdateResult.Success();
    }

    public async Task<CampUpdateResult> RemoveHistoricalNameAsync(
        Guid scopedCampId, Guid historicalNameId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var camp = await _repo.GetByIdAsync(scopedCampId, cancellationToken);
        if (camp is null) return CampUpdateResult.Failure("Camps_Flash_CampNotFound");
        var entry = camp.HistoricalNames.FirstOrDefault(n => n.Id == historicalNameId);
        if (entry is null)
        {
            return CampUpdateResult.Failure("Camps_Flash_HistoricalNameWrongCamp");
        }

        var removed = await _repo.RemoveHistoricalNameAsync(historicalNameId, cancellationToken);
        if (!removed)
        {
            return CampUpdateResult.Failure("Camps_Flash_HistoricalNameNotFound");
        }
        await _auditLog.LogAsync(
            CampsAuditActions.CampHistoricalNameRemoved, nameof(CampHistoricalName), historicalNameId,
            $"Removed historical name '{entry.Name}'", actorUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));
        return CampUpdateResult.Success();

    }

    public async Task<CampSeasonInfo?> GetCampSeasonByIdAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var season = await _repo.GetSeasonByIdAsync(campSeasonId, cancellationToken);
        if (season is null) return null;
        var roles = await _readProjection.GetRoleProjectionForYearsAsync([season.Year], cancellationToken);
        return CampReadProjection.CreateCampSeasonInfo(
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
        Guid campId, List<Guid> imageIdsInOrder, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _imageGallery.ReorderImagesAsync(campId, imageIdsInOrder, actorUserId, cancellationToken);

    public async Task<IReadOnlyList<CampEventRegistration>> GetRegistrationByEventAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = (await _repo.GetEventSettingsAsync(cancellationToken)).ToDictionary(r => r.EventSettingsId);
        return [.. (await _settingsService.GetAllEventSettingsAsync(cancellationToken))
            .Where(e => e.Status != EventSettingsStatus.Deleted)
            .OrderByDescending(e => e.GateOpeningDate)
            .Select(e => new CampEventRegistration(e.Id, e.EventName, e.Year,
                rows.TryGetValue(e.Id, out var row) && row.RegistrationOpen))];
    }

    public Task OpenSeasonAsync(Guid eventSettingsId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        SetRegistrationOpenAsync(eventSettingsId, open: true, actorUserId, cancellationToken);

    public Task CloseSeasonAsync(Guid eventSettingsId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        SetRegistrationOpenAsync(eventSettingsId, open: false, actorUserId, cancellationToken);

    private async Task SetRegistrationOpenAsync(
        Guid eventSettingsId, bool open, Guid actorUserId, CancellationToken cancellationToken)
    {
        var target = (await GetRegistrationByEventAsync(cancellationToken))
            .FirstOrDefault(r => r.EventSettingsId == eventSettingsId)
            ?? throw new InvalidOperationException($"Event {eventSettingsId} not found.");
        // Complete the save and audit together even if the caller stops waiting.
        var changed = await _repo.SetRegistrationOpenAsync(eventSettingsId, open, CancellationToken.None);
        if (changed && target.RegistrationOpen != open)
        {
            await _auditLog.LogAsync(
                open ? CampsAuditActions.CampRegistrationOpened : CampsAuditActions.CampRegistrationClosed,
                nameof(CampEventSettings), eventSettingsId,
                $"{(open ? "Opened" : "Closed")} camp registration for {target.EventName}", actorUserId);
        }
    }

    public async Task SetNameLockDateAsync(
        Guid eventSettingsId, LocalDate lockDate, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(cancellationToken)
            ?? throw new InvalidOperationException("Camp settings not found.");
        var registration = await GetRegistrationByEventAsync(cancellationToken);
        var targetEvent = registration.FirstOrDefault(r => r.EventSettingsId == eventSettingsId)
            ?? throw new InvalidOperationException($"Event {eventSettingsId} not found.");
        // Complete the save and audit together even if the caller stops waiting.
        var changed = await _repo.SetNameLockDateForEventAsync(
            eventSettingsId, lockDate, CancellationToken.None);
        if (changed > 0)
        {
            await _auditLog.LogAsync(CampsAuditActions.CampNameLockDateChanged, nameof(CampSettings), settings.Id,
                $"Set name lock date for {targetEvent.EventName} to {lockDate.ToInvariantDate()} on {changed} camp seasons", actorUserId);
        }
    }

    internal async Task<CampUpdateResult> ChangeSeasonNameAsync(
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

    public Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default) =>
        _memberships.RequestCampMembershipAsync(campId, userId, cancellationToken);

    public Task<CampMembershipMutationResult> ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default) =>
        _memberships.ApproveCampMemberAsync(scopedCampId, campMemberId, approvedByUserId, cancellationToken);

    public Task<CampMembershipMutationResult> RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default) =>
        _memberships.RejectCampMemberAsync(scopedCampId, campMemberId, rejectedByUserId, cancellationToken);

    public Task<CampMembershipMutationResult> RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default) =>
        _memberships.RemoveCampMemberAsync(scopedCampId, campMemberId, removedByUserId, cancellationToken);

    public Task<AddCampMemberOutcome> AddCampMemberToActiveSeasonAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, Guid? eventSettingsId = null) =>
        _memberships.AddCampMemberToActiveSeasonAsync(campId, userId, actorUserId, cancellationToken, eventSettingsId);

    public Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default, Guid? eventSettingsId = null) =>
        _memberships.AddMemberAndAssignRoleInActiveSeasonAsync(campId, roleDefinitionId, userId, actorUserId, cancellationToken, eventSettingsId);

    public Task<CampMembershipMutationResult> WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default) =>
        _memberships.WithdrawCampMembershipRequestAsync(campMemberId, userId, cancellationToken);

    public Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default) =>
        _memberships.LeaveCampAsync(campMemberId, userId, cancellationToken);

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

    public Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
        Guid scopedCampId, Guid campMemberId, bool granted, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        _memberships.SetEarlyEntryAsync(scopedCampId, campMemberId, granted, actorUserId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken cancellationToken = default) =>
        _repo.GetActiveLeadUserIdsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _repo.IsLeadAnywhereAsync(userId, cancellationToken);
}
