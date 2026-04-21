using NodaTime;

namespace Humans.Application.DTOs.Calendar;

public sealed record OverrideOccurrenceDto(
    Instant? OverrideStartUtc,
    Instant? OverrideEndUtc,
    string? OverrideTitle,
    string? OverrideDescription,
    string? OverrideLocation,
    string? OverrideLocationUrl);
