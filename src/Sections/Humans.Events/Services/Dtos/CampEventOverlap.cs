using Humans.Events.Domain;
using NodaTime;
using Humans.Events.Contracts;

namespace Humans.Events.Services.Dtos;

internal sealed record CampEventOverlap(
    Guid Id,
    Guid? CampId,
    string Title,
    Instant StartAt,
    int DurationMinutes,
    EventStatus Status);
