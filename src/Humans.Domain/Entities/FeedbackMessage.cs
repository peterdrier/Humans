using NodaTime;

namespace Humans.Domain.Entities;

public class FeedbackMessage
{
    public Guid Id { get; init; }

    public Guid FeedbackReportId { get; init; }
    public FeedbackReport FeedbackReport { get; set; } = null!;

    public Guid? SenderUserId { get; init; }

    /// <summary>
    /// Cross-domain navigation to the sender's <see cref="User"/>.
    /// Service stitches this in memory when rendering messages; repositories
    /// must not <c>.Include()</c> it.
    /// </summary>
    [Obsolete("Cross-domain nav — resolve via IUserService instead of navigating FeedbackMessage.SenderUser. See design-rules §6c.")]
    public User? SenderUser { get; set; }

    public string Content { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }
}
