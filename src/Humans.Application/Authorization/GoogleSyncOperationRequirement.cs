using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization requirement for Google Workspace sync operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, operationName, requirement)
/// where the resource is the target operation name string (for audit/logging).
///
/// Two operation classes:
/// - <see cref="Preview"/> — read-only operations that call the Google API but do not
///   mutate remote state (e.g. listing groups, checking drift, previewing a sync).
/// - <see cref="Execute"/> — operations that mutate Google Workspace state (add/remove
///   members, provision resources, remediate drift, sync settings changes).
/// </summary>
public sealed class GoogleSyncOperationRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Read-only Google API access (preview, enumerate, detect drift).
    /// </summary>
    public static readonly GoogleSyncOperationRequirement Preview = new(nameof(Preview));

    /// <summary>
    /// Mutating Google API access (add/remove members, remediate, provision, sync settings).
    /// </summary>
    public static readonly GoogleSyncOperationRequirement Execute = new(nameof(Execute));

    public string OperationName { get; }

    private GoogleSyncOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
