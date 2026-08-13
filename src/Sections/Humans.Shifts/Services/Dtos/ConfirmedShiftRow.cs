using NodaTime;

namespace Humans.Shifts.Services.Dtos;

internal sealed record ConfirmedShiftRow(
    Guid UserId,
    Guid TeamId,
    Instant StartsAtUtc,
    Instant EndsAtUtc);
