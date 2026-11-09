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
    internal const string CampRoleAssignments = CampDataLifecycle.CampRoleAssignments;
    internal const string CampMemberships = CampDataLifecycle.CampMemberships;

    private readonly ICampRepository _repo;
    private readonly IAuditLogService _auditLog;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly IFileStorage _fileStorage;
    private readonly CampImageGallery _imageGallery;
    private readonly CampReadProjection _readProjection;
    private readonly CampDataLifecycle _dataLifecycle;
    private readonly CampMembershipTransitions _memberships;
    private readonly CampSeasonLifecycle _seasons;
    // Lazy: CityPlanningService injects ICampService, closing the cycle.
    private readonly Lazy<ICityPlanningWrite> _cityPlanningService;
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
        _readProjection = new CampReadProjection(repo, settingsService, clock);
        _dataLifecycle = new CampDataLifecycle(repo, userService, systemTeamSync);
        _auditLog = auditLog;
        _systemTeamSync = systemTeamSync;
        _fileStorage = fileStorage;
        _imageGallery = new CampImageGallery(repo, fileStorage, clock, auditLog, logger);
        var membershipNotices = new CampMembershipNotices(repo, userService, notificationEmitter, logger);
        _seasons = new CampSeasonLifecycle(repo, auditLog, settingsService, membershipNotices, clock);
        _memberships = new CampMembershipTransitions(repo, auditLog, userService, settingsService, tickets,
            campRoleService, membershipNotices, clock);
        _cityPlanningService = cityPlanningService;
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

        var season = CampSeasonLifecycle.CreateSeasonFromData(camp.Id, targetEvent.Year, name, seasonData, now);
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

    public Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        _readProjection.GetCampBySlugAsync(slug, cancellationToken);

    public Task<CampInfo?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default) =>
        _readProjection.GetCampByIdAsync(campId, cancellationToken);

    public Task<CampEditData?> GetCampEditDataAsync(
        Guid campId, Guid? eventSettingsId = null, CancellationToken cancellationToken = default) =>
        _readProjection.GetCampEditDataAsync(campId, eventSettingsId, cancellationToken);

    public Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(
        int year, CancellationToken cancellationToken = default) =>
        _readProjection.GetCampsForYearAsync(year, cancellationToken);

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

    public Task<CampWriteResult<CampSeason>> OptInToSeasonAsync(
        Guid campId, Guid eventSettingsId, Guid? actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.OptInToSeasonAsync(campId, eventSettingsId, actorUserId, cancellationToken);

    public Task<CampUpdateResult> UpdateSeasonAsync(
        Guid scopedCampId, Guid seasonId, CampSeasonData data, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.UpdateSeasonAsync(scopedCampId, seasonId, data, actorUserId, cancellationToken);

    public Task<CampAdminMutationResult> ApproveSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default) =>
        _seasons.ApproveSeasonAsync(seasonId, reviewedByUserId, notes, cancellationToken);

    public Task<CampAdminMutationResult> RejectSeasonAsync(
        Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default) =>
        _seasons.RejectSeasonAsync(seasonId, reviewedByUserId, notes, cancellationToken);

    public Task<CampUpdateResult> WithdrawSeasonAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.WithdrawSeasonAsync(scopedCampId, seasonId, actorUserId, cancellationToken);

    public Task<CampUpdateResult> MarkSeasonFullAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.MarkSeasonFullAsync(scopedCampId, seasonId, actorUserId, cancellationToken);

    public Task<CampAdminMutationResult> ReactivateSeasonAsync(Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.ReactivateSeasonAsync(seasonId, actorUserId, cancellationToken);

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
        IReadOnlyList<string>? deletedImagePaths = null;
        var attempted = false;
        try
        {
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

                attempted = true;
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

            return new(true);
        }
        finally
        {
            if (attempted)
            {
                var deleted = false;
                try
                {
                    // Verify after the transaction scope exits; a failed save may have committed or rolled back.
                    deleted = await _repo.GetByIdAsync(campId, CancellationToken.None) is null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not verify camp {CampId} deletion before image cleanup", campId);
                }

                if (deleted)
                {
                    foreach (var path in deletedImagePaths ?? camp.Images.Select(image => image.StoragePath).ToList())
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
                }
            }
        }
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

    public Task<CampSeasonInfo?> GetCampSeasonByIdAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        _readProjection.GetCampSeasonByIdAsync(campSeasonId, cancellationToken);

    public Task<CampMemberLookup?> GetCampMemberStatusAsync(
        Guid campMemberId, CancellationToken cancellationToken = default) =>
        _readProjection.GetCampMemberStatusAsync(campMemberId, cancellationToken);

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

    public Task<IReadOnlyList<CampEventRegistration>> GetRegistrationByEventAsync(
        CancellationToken cancellationToken = default) =>
        _seasons.GetRegistrationByEventAsync(cancellationToken);

    public Task OpenSeasonAsync(Guid eventSettingsId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.OpenSeasonAsync(eventSettingsId, actorUserId, cancellationToken);

    public Task CloseSeasonAsync(Guid eventSettingsId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.CloseSeasonAsync(eventSettingsId, actorUserId, cancellationToken);

    public Task SetNameLockDateAsync(
        Guid eventSettingsId, LocalDate lockDate, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.SetNameLockDateAsync(eventSettingsId, lockDate, actorUserId, cancellationToken);

    internal Task<CampUpdateResult> ChangeSeasonNameAsync(
        Guid scopedCampId, Guid seasonId, string newName, Guid actorUserId, CancellationToken cancellationToken = default) =>
        _seasons.ChangeSeasonNameAsync(scopedCampId, seasonId, newName, actorUserId, cancellationToken);

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

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        _dataLifecycle.ReassignAsync(sourceUserId, targetUserId, actorUserId, updatedAt, ct);

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        _dataLifecycle.ContributeForUserAsync(userId, ct);

    internal static readonly IReadOnlyDictionary<string, string?> Erasure = CampDataLifecycle.Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        _dataLifecycle.EraseForUserAsync(userId, ct);

    public Task<CampAdminMutationResult> SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        _seasons.SetCampSeasonEeSlotCountAsync(campSeasonId, slotCount, actorUserId, cancellationToken);

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
