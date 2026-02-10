using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// Volunteer history entry data for display purposes.
/// </summary>
public record VolunteerHistoryEntryDto(
    Guid Id,
    LocalDate Date,
    string EventName,
    string? Description);

/// <summary>
/// Volunteer history entry data for editing purposes.
/// </summary>
public record VolunteerHistoryEntryEditDto(
    Guid? Id,
    LocalDate Date,
    string EventName,
    string? Description);
