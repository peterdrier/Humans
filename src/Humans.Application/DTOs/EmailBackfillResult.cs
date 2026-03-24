namespace Humans.Application.DTOs;

public class EmailMismatch
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string StoredEmail { get; init; } = string.Empty;
    public string GoogleEmail { get; init; } = string.Empty;
}

public class EmailBackfillResult
{
    public List<EmailMismatch> Mismatches { get; init; } = [];
    public int TotalUsersChecked { get; init; }
    public string? ErrorMessage { get; init; }
}
