using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampDetails(
    ICampRepository repo,
    IAuditLogService auditLog,
    CampSeasonLifecycle seasons,
    IClock clock) : IApplicationService
{
    private const int MaxHistoricalNameLength = CampRegistration.MaxHistoricalNameLength;

    internal async Task<CampUpdateResult> UpdateCampAsync(
        CampUpdateInput input,
        Guid actorUserId, CancellationToken cancellationToken = default)
    {
        // Prove the season belongs to the camp before the first write: the scoped check
        // inside UpdateSeasonAsync fires only after the camp-level fields have committed,
        // which would leave a partial update behind an uninvalidated cache on failure.
        var scopedSeason = await repo.GetSeasonByIdAsync(input.SeasonId, cancellationToken);
        if (scopedSeason is null)
        {
            return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");
        }

        if (scopedSeason.CampId != input.CampId)
        {
            return CampUpdateResult.Failure("Camps_Flash_SeasonWrongCamp");
        }

        var updated = await repo.UpdateCampFieldsAsync(
            input.CampId,
            input.ContactEmail,
            input.ContactPhone,
            input.WebOrSocialUrl,
            input.Links,
            input.IsSwissCamp,
            input.TimesAtNowhere,
            input.HideHistoricalNames,
            clock.GetCurrentInstant(),
            cancellationToken);

        if (!updated)
        {
            return CampUpdateResult.Failure("Camps_Flash_CampNotFound");
        }

        await auditLog.LogAsync(
            CampsAuditActions.CampUpdated, nameof(Camp), input.CampId,
            $"Updated camp {input.CampId}",
            actorUserId);

        var seasonUpdate = await seasons.UpdateSeasonAsync(input.CampId, input.SeasonId, input.SeasonData, actorUserId, cancellationToken);
        if (!seasonUpdate.Succeeded) return seasonUpdate;

        var currentSeason = await repo.GetSeasonByIdAsync(input.SeasonId, cancellationToken);
        if (currentSeason is null) return CampUpdateResult.Failure("Camps_Flash_RoleSeasonNotFound");

        if (!string.Equals(currentSeason.Name, input.SeasonName, StringComparison.Ordinal))
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            var nameLocked = currentSeason.NameLockDate.HasValue && today >= currentSeason.NameLockDate.Value;
            if (!nameLocked)
            {
                var nameUpdate = await seasons.ChangeSeasonNameAsync(input.CampId, currentSeason.Id, input.SeasonName, actorUserId, cancellationToken);
                if (!nameUpdate.Succeeded) return nameUpdate;
            }
        }

        return CampUpdateResult.Success();
    }

    internal async Task<CampUpdateResult> AddHistoricalNameAsync(
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
            CreatedAt = clock.GetCurrentInstant()
        };

        await repo.AddHistoricalNameAsync(entry, cancellationToken);
        await auditLog.LogAsync(
            CampsAuditActions.CampHistoricalNameAdded, nameof(CampHistoricalName), entry.Id,
            $"Added historical name '{name}'", actorUserId,
            relatedEntityId: campId, relatedEntityType: nameof(Camp));
        return CampUpdateResult.Success();
    }

    internal async Task<CampUpdateResult> RemoveHistoricalNameAsync(
        Guid scopedCampId, Guid historicalNameId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var camp = await repo.GetByIdAsync(scopedCampId, cancellationToken);
        if (camp is null) return CampUpdateResult.Failure("Camps_Flash_CampNotFound");
        var entry = camp.HistoricalNames.FirstOrDefault(n => n.Id == historicalNameId);
        if (entry is null)
        {
            return CampUpdateResult.Failure("Camps_Flash_HistoricalNameWrongCamp");
        }

        var removed = await repo.RemoveHistoricalNameAsync(historicalNameId, cancellationToken);
        if (!removed)
        {
            return CampUpdateResult.Failure("Camps_Flash_HistoricalNameNotFound");
        }
        await auditLog.LogAsync(
            CampsAuditActions.CampHistoricalNameRemoved, nameof(CampHistoricalName), historicalNameId,
            $"Removed historical name '{entry.Name}'", actorUserId,
            relatedEntityId: scopedCampId, relatedEntityType: nameof(Camp));
        return CampUpdateResult.Success();

    }

}
