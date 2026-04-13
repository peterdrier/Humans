using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization requirement for Google Workspace sync operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, operationName, requirement)
/// where the resource is the target operation name string (for audit/logging).
///
/// Three operation classes, corresponding to the Teams section invariants:
/// - <see cref="Preview"/> — read-only operations that call the Google API but do not
///   mutate remote state (e.g. listing groups, checking drift, previewing a sync).
/// - <see cref="TeamResource"/> — team-scoped resource management (link/unlink a team's
///   Google Group or Drive folder, provision on team creation). TeamsAdmin is explicitly
///   authorized for these per docs/sections/Teams.md ("link/unlink Google resources on
///   all teams").
/// - <see cref="Execute"/> — sync actions that mutate workspace-wide state (bulk sync,
///   reconciliation, drift remediation, settings changes). Admin-only per the Teams
///   invariant ("TeamsAdmin cannot execute sync actions").
/// </summary>
public sealed class GoogleSyncOperationRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Read-only Google API access (preview, enumerate, detect drift).
    /// </summary>
    public static readonly GoogleSyncOperationRequirement Preview = new(nameof(Preview));

    /// <summary>
    /// Team-scoped Google resource management (link/unlink team groups and drive folders).
    /// </summary>
    public static readonly GoogleSyncOperationRequirement TeamResource = new(nameof(TeamResource));

    /// <summary>
    /// Workspace-wide sync actions (bulk sync, reconciliation, remediation, settings changes).
    /// </summary>
    public static readonly GoogleSyncOperationRequirement Execute = new(nameof(Execute));

    public string OperationName { get; }

    private GoogleSyncOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
