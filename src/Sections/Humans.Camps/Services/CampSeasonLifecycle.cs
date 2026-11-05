using Humans.AuditLog.Contracts;
using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampSeasonLifecycle(
    ICampRepository repo,
    IAuditLogService auditLog,
    ISettingsService settingsService,
    CampMembershipNotices membershipNotices,
    IClock clock) : IApplicationService
{
    public async Task<CampWriteResult<CampSeason>> OptInToSeasonAsync(
        Guid campId, Guid eventSettingsId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var registration = await GetRegistrationByEventAsync(cancellationToken);
        var targetEvent = registration.FirstOrDefault(r => r.EventSettingsId == eventSettingsId);
        if (targetEvent is not { RegistrationOpen: true })
        {
            return new(null, "Camps_Flash_SeasonNotOpen");
        }

        if (await repo.SeasonExistsAsync(
                campId, eventSettingsId, cancellationToken))
        {
            return new(null, "Camps_Flash_SeasonAlreadyExists");
        }

        var previousSeason = await repo.GetLatestSeasonAsync(campId, cancellationToken);
        if (previousSeason is null) return new(null, "Camps_Flash_NoPreviousSeason");

        var hasApprovedSeason = await repo.HasApprovedSeasonAsync(campId, cancellationToken);

        var now = clock.GetCurrentInstant();
        var newSeason = hasApprovedSeason
            ? previousSeason.CreateApprovedRenewal(Guid.NewGuid(), targetEvent.Year, now)
            : previousSeason.CreatePendingRenewal(Guid.NewGuid(), targetEvent.Year, now);

        newSeason.EventSettingsId = eventSettingsId;
        await repo.AddSeasonAsync(newSeason, cancellationToken);

        var description = $"Opted in to season {targetEvent.EventName} (auto-approved: {hasApprovedSeason})";
        if (actorUserId is { } actorId)
        {
            await auditLog.LogAsync(
                CampsAuditActions.CampSeasonCreated, nameof(CampSeason), newSeason.Id,
                description, actorId,
                relatedEntityId: campId, relatedEntityType: nameof(Camp));
        }
        else
        {
            // Dev-persona provisioning runs before sign-in and has no human actor.
            await auditLog.LogAsync(
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
        var now = clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await repo.UpdateSeasonAsync(seasonId, season =>
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

        await auditLog.LogAsync(
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
        var now = clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await repo.UpdateSeasonAsync(seasonId, season =>
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

        await auditLog.LogAsync(
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
        var now = clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await repo.UpdateSeasonAsync(seasonId, season =>
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

        await auditLog.LogAsync(
            CampsAuditActions.CampSeasonRejected, nameof(CampSeason), seasonId,
            $"Rejected season {year}: {notes}",
            reviewedByUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await membershipNotices.NotifyPendingRequestersOfSeasonClosureAsync(seasonId, campId, year);
        return new(true);

    }

    public async Task<CampUpdateResult> WithdrawSeasonAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusalKey = null;
        var now = clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;

        var found = await repo.UpdateSeasonAsync(seasonId, season =>
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

        await auditLog.LogAsync(
            CampsAuditActions.CampSeasonWithdrawn, nameof(CampSeason), seasonId,
            $"Withdrew from season {year}",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        await membershipNotices.NotifyPendingRequestersOfSeasonClosureAsync(seasonId, campId, year);

        return CampUpdateResult.Success();

    }

    public async Task<CampUpdateResult> MarkSeasonFullAsync(
        Guid scopedCampId, Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusalKey = null;
        var now = clock.GetCurrentInstant();
        var year = 0;

        var found = await repo.UpdateSeasonAsync(seasonId, season =>
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

        await auditLog.LogAsync(
            CampsAuditActions.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {year} status set to Full",
            actorUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));

        return CampUpdateResult.Success();

    }

    public async Task<CampAdminMutationResult> ReactivateSeasonAsync(Guid seasonId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusal = null;
        var now = clock.GetCurrentInstant();
        var year = 0;
        var campId = Guid.Empty;
        var previousStatus = CampSeasonStatus.Pending;
        var newStatus = CampSeasonStatus.Pending;

        var found = await repo.UpdateSeasonAsync(seasonId, season =>
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

        await auditLog.LogAsync(
            CampsAuditActions.CampSeasonStatusChanged, nameof(CampSeason), seasonId,
            $"Season {year} status changed from {previousStatus} to {newStatus}",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
        return new(true);

    }

    public async Task<IReadOnlyList<CampEventRegistration>> GetRegistrationByEventAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = (await repo.GetEventSettingsAsync(cancellationToken)).ToDictionary(r => r.EventSettingsId);
        return [.. (await settingsService.GetAllEventSettingsAsync(cancellationToken))
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
        var changed = await repo.SetRegistrationOpenAsync(eventSettingsId, open, CancellationToken.None);
        if (changed && target.RegistrationOpen != open)
        {
            await auditLog.LogAsync(
                open ? CampsAuditActions.CampRegistrationOpened : CampsAuditActions.CampRegistrationClosed,
                nameof(CampEventSettings), eventSettingsId,
                $"{(open ? "Opened" : "Closed")} camp registration for {target.EventName}", actorUserId);
        }
    }

    public async Task SetNameLockDateAsync(
        Guid eventSettingsId, LocalDate lockDate, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var settings = await repo.GetSettingsReadOnlyAsync(cancellationToken)
            ?? throw new InvalidOperationException("Camp settings not found.");
        var registration = await GetRegistrationByEventAsync(cancellationToken);
        var targetEvent = registration.FirstOrDefault(r => r.EventSettingsId == eventSettingsId)
            ?? throw new InvalidOperationException($"Event {eventSettingsId} not found.");
        // Complete the save and audit together even if the caller stops waiting.
        var changed = await repo.SetNameLockDateForEventAsync(
            eventSettingsId, lockDate, CancellationToken.None);
        if (changed > 0)
        {
            await auditLog.LogAsync(CampsAuditActions.CampNameLockDateChanged, nameof(CampSettings), settings.Id,
                $"Set name lock date for {targetEvent.EventName} to {lockDate.ToInvariantDate()} on {changed} camp seasons", actorUserId);
        }
    }

    internal async Task<CampUpdateResult> ChangeSeasonNameAsync(
        Guid scopedCampId, Guid seasonId, string newName, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        string? refusalKey = null;
        var now = clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        string? oldName = null;
        var campId = Guid.Empty;

        var found = await repo.ApplyNameChangeAsync(seasonId, season =>
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

        await auditLog.LogAsync(
            CampsAuditActions.CampNameChanged, nameof(CampSeason), seasonId,
            $"Name changed from '{oldName}' to '{newName}'",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));

        return CampUpdateResult.Success();

    }

    internal static CampSeason CreateSeasonFromData(
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

    public async Task<CampAdminMutationResult> SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (slotCount < 0)
            return new(false, "EE slot count cannot be negative.");

        var result = await repo.SetCampSeasonEeSlotCountAsync(campSeasonId, slotCount, cancellationToken);
        if (result is null)
            return new(false, "Camp season not found.");

        var (oldValue, newValue, campId) = result.Value;
        if (oldValue == newValue) return new(true);

        await auditLog.LogAsync(
            CampsAuditActions.CampSeasonEeSlotCountChanged,
            nameof(CampSeason), campSeasonId,
            $"EE slot count changed from {oldValue} to {newValue}.",
            actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
        return new(true);
    }
}
