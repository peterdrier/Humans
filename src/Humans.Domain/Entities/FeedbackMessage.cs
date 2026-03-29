using NodaTime;

namespace Humans.Domain.Entities;

public class FeedbackMessage
{
    public Guid Id { get; init; }

    public Guid FeedbackReportId { get; init; }
    public FeedbackReport FeedbackReport { get; set; } = null!;

    public Guid? SenderUserId { get; init; }
    public User? SenderUser { get; set; }

    public string Content { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }
}
