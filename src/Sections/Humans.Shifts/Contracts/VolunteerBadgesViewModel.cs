namespace Humans.Shifts.Contracts;

/// <summary>Public because widget-gallery sample cards construct it.</summary>
public sealed record VolunteerBadgesViewModel(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages,
    string? DietaryPreference);
