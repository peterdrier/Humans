using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>
/// <paramref name="Id"/> matches <c>VolunteerHistoryEntry.Id</c>: existing rows round-trip with it, new rows post
/// <see cref="Guid.Empty"/> and get a fresh id on insert.
/// </summary>
public record CVEntry(Guid Id, LocalDate Date, string EventName, string? Description);
