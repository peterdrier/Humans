namespace Humans.GoogleIntegration.Services;

/// <summary>The stored GoogleEmail differs from Google's current primaryEmail.</summary>
internal sealed class EmailRenameInfo
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string OldEmail { get; init; } = string.Empty;
    public string NewEmail { get; init; } = string.Empty;
    public int AffectedResourceCount { get; init; }
}

internal sealed class EmailRenameDetectionResult
{
    public List<EmailRenameInfo> Renames { get; init; } = [];
    public int TotalUsersChecked { get; init; }
    public string? ErrorMessage { get; init; }
}
