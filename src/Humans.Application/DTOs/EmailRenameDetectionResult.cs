namespace Humans.Application.DTOs;

/// <summary>
/// A detected email rename: the user's stored GoogleEmail differs from
/// the current primaryEmail returned by Google Directory API.
/// </summary>
public class EmailRenameInfo
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string OldEmail { get; init; } = string.Empty;
    public string NewEmail { get; init; } = string.Empty;
    public int AffectedResourceCount { get; init; }
}

/// <summary>
/// Result of scanning for renamed @nobodies.team emails.
/// </summary>
public class EmailRenameDetectionResult
{
    public List<EmailRenameInfo> Renames { get; init; } = [];
    public int TotalUsersChecked { get; init; }
    public string? ErrorMessage { get; init; }
}
