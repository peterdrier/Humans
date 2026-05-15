using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.Events;

public record CampEventOverlap(
    Guid Id,
    Guid? CampId,
    string Title,
    Instant StartAt,
    int DurationMinutes,
    EventStatus Status);
