using NodaTime;

namespace Humans.Domain.Entities;

public class IssueComment
{
    public Guid Id { get; init; }
    public Guid IssueId { get; init; }
    public Issue Issue { get; set; } = null!; // aggregate-local nav, .Include() is legal

    public Guid? SenderUserId { get; init; }

    [Obsolete("Cross-domain nav — resolve via IUserService instead of navigating IssueComment.SenderUser. See design-rules §6c.")]
    public User? SenderUser { get; set; }

    public string Content { get; set; } = string.Empty;
    public Instant CreatedAt { get; init; }
}
