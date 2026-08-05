using NodaTime;

namespace Humans.Domain.Entities;

public class IssueComment
{
    public Guid Id { get; init; }
    public Guid IssueId { get; init; }
    public Issue Issue { get; set; } = null!; // aggregate-local nav, .Include() is legal

    public Guid? SenderUserId { get; init; }

    public string Content { get; set; } = string.Empty;
    public Instant CreatedAt { get; init; }
}
