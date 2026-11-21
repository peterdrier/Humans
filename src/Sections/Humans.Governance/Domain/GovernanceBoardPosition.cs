using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>Who holds a Board office. Shown only while the holder has an active Board seat.</summary>
internal sealed class GovernanceBoardPosition
{
    public BoardPosition Position { get; set; }
    public Guid UserId { get; set; }
    public Instant UpdatedAt { get; set; }
}
