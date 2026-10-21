using Humans.Base.Interfaces;
using Humans.Base.Attributes;
using Humans.Base.Enums;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// The Google provisioning writes other sections make, alongside the read-only
/// <see cref="IGoogleSyncService"/>: team-resource grants and removals,
/// team group provisioning, and Drive subfolder creation and sync requests. Everything else the
/// sync service does is internal to <c>Humans.GoogleIntegration</c>.
/// </summary>
public interface IGoogleSyncWrite : IApplicationService
{
    /// <summary>
    /// Adds a user to all Google resources associated with a team. Only an accepted
    /// Drive grant validates the address; queued group work and disabled sync are deferred.
    /// </summary>
    /// <param name="teamId">The team ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="syncSource">The trigger recorded on any Drive sync-log rows.</param>
    [ExternalWrite]
    Task<GoogleResourceGrantResult> AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync);

    /// <summary>
    /// Ensures a team has a linked Google Group. If GoogleGroupPrefix is set but no Group
    /// resource exists, creates or links the group. Called when prefix is set on a team.
    /// </summary>
    [ExternalWrite]
    Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new Drive subfolder named <paramref name="name"/> directly
    /// under <paramref name="parentFolderId"/> and returns its Google file
    /// id. Used by sections provisioning a Drive folder outside the
    /// Teams-keyed <c>google_resources</c> path (e.g. Workgroups registering
    /// a group). Respects the <c>GoogleDrive</c> <c>SyncMode</c> — throws
    /// when sync is disabled or the Google API call fails, so a failed
    /// creation is visible to the caller rather than returning a bogus id.
    /// </summary>
    [ExternalWrite]
    Task<string> CreateSubfolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests an on-demand Drive access reconcile for one folder claimed
    /// through the <see cref="IGoogleDriveAccessSource"/> fan-out (e.g.
    /// Teams or Workgroups, after a membership or status change). Deferred: returns
    /// once the reconcile is scheduled, not once it has run.
    /// </summary>
    Task RequestSyncAsync(string folderId, CancellationToken cancellationToken = default);
}

public sealed record GroupSettingsRemediationResult(bool Succeeded, string? ErrorMessage)
{
    public static GroupSettingsRemediationResult Success() => new(true, null);

    public static GroupSettingsRemediationResult Failure(string message) => new(false, message);
}

/// <summary>Whether team provisioning actually established acceptance of the Google address.</summary>
public enum GoogleResourceGrantOutcome
{
    /// <summary>No immediate vendor acceptance: no resources, disabled Drive sync or queued group work.</summary>
    Deferred,
    /// <summary>Google accepted at least one Drive grant and none failed.</summary>
    Accepted,
    /// <summary>A grant failed or the user has no eligible address.</summary>
    Failed
}

/// <summary>Vendor acceptance and the exact Google identity address attempted by team provisioning.</summary>
/// <param name="Outcome">Accepted only when at least one Drive grant succeeds and none fail.</param>
/// <param name="UserId">Resolved owner of the attempted address.</param>
/// <param name="GoogleEmail">Attempted Google identity address; required for Accepted.</param>
/// <param name="Retryable">Whether any failed grant can be retried; local refusals are permanent.</param>
/// <param name="ErrorMessage">Failure details for the outbox and operator diagnostics.</param>
public sealed record GoogleResourceGrantResult(
    GoogleResourceGrantOutcome Outcome,
    Guid UserId,
    string? GoogleEmail = null,
    bool Retryable = false,
    string? ErrorMessage = null);
