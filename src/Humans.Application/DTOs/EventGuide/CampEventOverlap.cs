using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.EventGuide;

public record CampEventOverlap(
    Guid Id,
    Guid? CampId,
    string Title,
    Instant StartAt,
    int DurationMinutes,
    GuideEventStatus Status);
