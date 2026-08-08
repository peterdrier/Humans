using NodaTime;

namespace Humans.Domain.Entities;

public class FeedbackMessage
{
    public Guid Id { get; init; }

    public Guid FeedbackReportId { get; init; }
    public FeedbackReport FeedbackReport { get; set; } = null!;

    /// <summary>
    /// FK column only — no navigation property. Resolve the sender's
    /// display name via <c>IUserServiceRead.GetUserInfosAsync</c>.
    /// </summary>
    public Guid? SenderUserId { get; init; }

    public string Content { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }
}
