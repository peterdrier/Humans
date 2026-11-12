namespace Humans.Shifts.Models;

/// <summary>Public because widget-gallery sample cards construct it.</summary>
internal sealed record VolunteerBadgesViewModel(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages);
