namespace Humans.Teams.Contracts;

public enum SlotPriority
{
    None = 0,

    /// <summary>Must be filled: critical for team function.</summary>
    Critical = 1,

    /// <summary>Should be filled if possible.</summary>
    Important = 2,

    /// <summary>Helpful but not essential.</summary>
    NiceToHave = 3
}
