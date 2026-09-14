using NodaTime;

namespace Humans.Calendar.Services.Dtos;

internal sealed record OverrideOccurrenceDto(
    Instant? OverrideStartUtc,
    Instant? OverrideEndUtc,
    string? OverrideTitle,
    string? OverrideDescription,
    string? OverrideLocation,
    string? OverrideLocationUrl,
    LocalDate? OverrideStartDate = null,
    LocalDate? OverrideEndDateExclusive = null);
