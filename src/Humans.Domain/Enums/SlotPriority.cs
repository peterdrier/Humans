namespace Humans.Domain.Enums;

/// <summary>
/// Priority level for a team role slot.
/// </summary>
public enum SlotPriority
{
    /// <summary>
    /// No priority assigned.
    /// </summary>
    None = 0,

    /// <summary>
    /// Must be filled — critical for team function.
    /// </summary>
    Critical = 1,

    /// <summary>
    /// Should be filled if possible.
    /// </summary>
    Important = 2,

    /// <summary>
    /// Helpful but not essential.
    /// </summary>
    NiceToHave = 3
}
