using Humans.Base.Interfaces;
using Humans.Base.Attributes;
using Humans.Base.Enums;

namespace Humans.GoogleIntegration.Contracts;

public interface IGoogleSyncWrite : IApplicationService
{
    /// <summary>
    /// Only an accepted Drive grant validates the address; queued group work and disabled sync are deferred.
    /// </summary>
    [ExternalWrite]
    Task<GoogleResourceGrantResult> AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync);

    /// <summary>Creates or links the group when GoogleGroupPrefix is set and no Group resource exists.</summary>
    [ExternalWrite]
    Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the new folder's id. Throws when Drive sync is disabled or the API call fails, never returning a
    /// bogus id.
    /// </summary>
    [ExternalWrite]
    Task<string> CreateSubfolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Deferred: returns once the reconcile is scheduled, not once it has run.</summary>
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

/// <summary>
/// <paramref name="GoogleEmail"/> is the address attempted; required for Accepted. Local refusals are never
/// <paramref name="Retryable"/>.
/// </summary>
public sealed record GoogleResourceGrantResult(
    GoogleResourceGrantOutcome Outcome,
    Guid UserId,
    string? GoogleEmail = null,
    bool Retryable = false,
    string? ErrorMessage = null);
