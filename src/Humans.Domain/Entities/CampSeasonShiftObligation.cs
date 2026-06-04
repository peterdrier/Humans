namespace Humans.Domain.Entities;

/// <summary>
/// A per-season override of a <see cref="ShiftObligation"/>'s required shift count for a
/// specific camp season. Owned by the Camps section.
/// </summary>
public class CampSeasonShiftObligation
{
    public Guid Id { get; init; }

    public Guid CampSeasonId { get; init; }

    public Guid ShiftObligationId { get; init; }
    public ShiftObligation ShiftObligation { get; set; } = null!;

    public int RequiredShiftCount { get; set; }
}
