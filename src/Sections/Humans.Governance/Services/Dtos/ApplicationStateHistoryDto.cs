using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record ApplicationStateHistoryDto(
    ApplicationStatus Status,
    Instant ChangedAt,
    Guid ChangedByUserId,
    string? ChangedByDisplayName,
    string? Notes);
