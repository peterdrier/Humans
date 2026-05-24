using NodaTime;

namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record ConfirmedShiftRow(
    Guid UserId,
    Guid TeamId,
    Instant StartsAtUtc,
    Instant EndsAtUtc);
