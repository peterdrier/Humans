using System.Transactions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.CityPlanning.Contracts;

namespace Humans.Camps.Services;

internal sealed class CampDeletion(
    ICampRepository repo,
    IAuditLogService auditLog,
    IFileStorage fileStorage,
    // Lazy: CityPlanningService injects ICampService, closing the cycle.
    Lazy<ICityPlanningWrite> cityPlanningService,
    ILogger<CampService> logger) : IApplicationService
{
    internal async Task<CampAdminMutationResult> DeleteCampAsync(Guid campId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var camp = await repo.GetByIdAsync(campId, cancellationToken);
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
                    var removed = await cityPlanningService.Value
                        .DeleteCampPolygonsForSeasonsAsync(seasonIds, cancellationToken);
                    if (removed > 0)
                    {
                        logger.LogInformation(
                            "Deleted {Rows} city-planning polygon/history rows for {Seasons} seasons of camp {CampId}",
                            removed, seasonIds.Count, campId);
                    }
                }

                attempted = true;
                deletedImagePaths = await repo.DeleteCampAsync(campId, cancellationToken);
                if (deletedImagePaths is null)
                {
                    return new(false, "Camp not found.");
                }

                scope.Complete();
            }

            await auditLog.LogAsync(
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
                    deleted = await repo.GetByIdAsync(campId, CancellationToken.None) is null;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not verify camp {CampId} deletion before image cleanup", campId);
                }

                if (deleted)
                {
                    foreach (var path in deletedImagePaths ?? camp.Images.Select(image => image.StoragePath).ToList())
                    {
                        try
                        {
                            await fileStorage.DeleteAsync(path, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex,
                                "Failed to delete camp image file at {StoragePath} during camp delete for {CampId}; DB row already removed",
                                path, campId);
                        }
                    }
                }
            }
        }
    }

}
