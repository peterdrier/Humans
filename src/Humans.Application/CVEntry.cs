using NodaTime;

namespace Humans.Application;

/// <summary>
/// Slim projection of a volunteer-history entry, as included in
/// <see cref="FullProfile"/>. Date is rendered as "MMM'yy" in the UI.
/// </summary>
/// <remarks>
/// <paramref name="Id"/> is the stable per-row identity matching
/// <see cref="Humans.Domain.Entities.VolunteerHistoryEntry.Id"/>. Existing rows
/// round-trip through the editor with their original Id; new rows post
/// <see cref="Guid.Empty"/> and are assigned a fresh Id on insert.
/// </remarks>
public record CVEntry(Guid Id, LocalDate Date, string EventName, string? Description);
