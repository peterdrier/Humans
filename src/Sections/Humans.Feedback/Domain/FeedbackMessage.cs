using NodaTime;

namespace Humans.Feedback.Domain;

internal sealed class FeedbackMessage
{
    public Guid Id { get; init; }

    public Guid FeedbackReportId { get; init; }
    public FeedbackReport FeedbackReport { get; set; } = null!;

    /// <summary>
    /// FK column only — no navigation property. Resolve the sender's
    /// display name via <c>IUserService.GetUserInfosAsync</c>.
    /// </summary>
    public Guid? SenderUserId { get; init; }

    public string Content { get; set; } = string.Empty;

    /// <summary>True until the reply is handed to Email; null marks historical messages.</summary>
    public bool? EmailEnqueuePending { get; set; }

    public Instant CreatedAt { get; init; }
}
