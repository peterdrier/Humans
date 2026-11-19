using NodaTime;

namespace Humans.Shifts.Services;

internal sealed record NoShowHistoryEntry(
    string ShiftLabel,
    Guid TeamId,
    Instant ShiftStart,
    string TimeZoneId,
    Guid? ReviewedByUserId,
    Instant? ReviewedAt);
