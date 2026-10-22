namespace Humans.Events.Contracts;

/// <summary>
/// Already scored by this section. A Title match scores 100/80/60 (exact/prefix/contains); a Description-only match
/// scores half that, so it always ranks below every Title match.
/// </summary>
/// <remarks><paramref name="Title"/> is only an alphabetical tiebreak and is never rendered.</remarks>
public record EventSearchHit(
    Guid EventId,
    string Title,
    int Score);
