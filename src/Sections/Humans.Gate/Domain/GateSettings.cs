using NodaTime;

namespace Humans.Gate.Domain;

/// <summary>The cutoff is an <see cref="Instant"/>, not a date: admission is decided to the minute.</summary>
internal sealed class GateSettings
{
    public int Id { get; init; }

    /// <summary>
    /// Before it, a valid ticket also needs Early Entry covering today. <see cref="Instant.MinValue"/> means not
    /// configured: scans fail safe to AMBER until an admin sets it.
    /// </summary>
    public Instant GeneralEntryOpensAt { get; set; } = Instant.MinValue;

    /// <summary>An unset cutoff is undecidable (AMBER), never "general entry already open".</summary>
    public bool IsCutoffConfigured => GeneralEntryOpensAt != Instant.MinValue;

    /// <summary>Display only: the number shown to the agent, not an age computed from a birth date.</summary>
    public int MinorAgeThresholdYears { get; set; } = 16;
}
