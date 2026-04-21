using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Composite key (UserId, Day). One row per user per calendar day in the
/// user's configured timezone (we approximate with UTC — Phase 3 revisits).
/// </summary>
public class AgentRateLimit
{
    public Guid UserId { get; init; }
    public User User { get; set; } = null!;

    public LocalDate Day { get; init; }

    public int MessagesToday { get; set; }
    public int TokensToday { get; set; }
}
