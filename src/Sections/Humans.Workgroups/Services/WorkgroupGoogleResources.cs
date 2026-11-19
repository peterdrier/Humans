using System.Text.RegularExpressions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Settings.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services.Contributors;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupGoogleResources(
    IWorkgroupRepository repository,
    ISettingsService settings,
    IGoogleSyncService googleSync,
    IGoogleSyncWrite googleSyncWrite,
    IGoogleGroupSync googleGroupSync,
    IAuditLogService auditLog,
    ILogger<WorkgroupService> logger) : IApplicationService
{
    internal async Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default)
    {
        var stored = await repository.GetRootDriveFolderIdAsync(ct)
            // Legacy Settings value, used until an admin saves a local one.
            ?? await settings.GetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, ct);
        // Values saved before the setter normalized may still hold the pasted URL.
        return stored is null ? null : NormalizeFolderId(stored);
    }

    internal async Task<WorkgroupMutationResult> SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderNotConfigured));

        var id = NormalizeFolderId(folderId);
        // A missing root folder blocks every registration, so refuse it now rather than then.
        if (!await googleSync.IsFolderReachableAsync(id, ct))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderUnreachable));

        await repository.SetRootDriveFolderIdAsync(id, ct);
        await auditLog.LogAsync(WorkgroupsAuditActions.WorkgroupsRootFolderUpdated,
            AuditEntityTypes.WorkgroupsSettings, Guid.Empty,
            $"Root Drive folder set to {id}", actorUserId);
        return new();
    }

    /// <summary>Secretaries paste the folder's browser URL as often as its id; only the id is kept.</summary>
    private static string NormalizeFolderId(string value) =>
        DriveFolderUrlId.Match(value) is { Success: true } m ? m.Groups["id"].Value : value.Trim();

    private static readonly Regex DriveFolderUrlId = new(@"(?:/folders/|[?&]id=)(?<id>[A-Za-z0-9_-]+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
    /// <summary>Neither failure (unset root, Google error) leaves the group half-registered.</summary>
    internal async Task<WorkgroupMutationResult<string>> CreateGroupFolderAsync(Workgroup workgroup, CancellationToken ct)
    {
        var root = await GetRootDriveFolderIdAsync(ct);
        if (string.IsNullOrWhiteSpace(root))
            return new(Refusal: new(WorkgroupErrorKeys.RootFolderNotConfigured));

        try
        {
            return new(await googleSyncWrite.CreateSubfolderAsync(root, workgroup.Name, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create the Drive subfolder for workgroup {WorkgroupId}",
                workgroup.Id);
            return new(Refusal: new(WorkgroupErrorKeys.DriveFolderCreationFailed));
        }
    }

    /// <summary>Failures are logged, never surfaced: the daily reconciliation covers the drift.</summary>
    internal async Task RequestGoogleSyncAsync(Workgroup w, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(w.DriveFolderId))
        {
            try
            {
                await googleSyncWrite.RequestSyncAsync(w.DriveFolderId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to request a Drive access sync for workgroup {WorkgroupId}", w.Id);
            }
        }

        // Only claimed groups: a scoped sync of an unclaimed key is recorded as an error.
        if (w.GoogleGroupEmail is { } groupKey && WorkgroupGoogleGroupSource.IsClaimed(w.Status))
        {
            try
            {
                await googleGroupSync.RequestSyncAsync(groupKey, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to request a Google group sync for workgroup {WorkgroupId}", w.Id);
            }
        }
    }
}
