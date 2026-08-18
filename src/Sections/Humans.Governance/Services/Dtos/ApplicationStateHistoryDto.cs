using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Stitched view of a single <c>ApplicationStateHistory</c> row with the
/// actor's display name resolved via <c>IUserService</c> at the service layer.
/// </summary>
internal sealed record ApplicationStateHistoryDto(
    ApplicationStatus Status,
    Instant ChangedAt,
    Guid ChangedByUserId,
    string? ChangedByDisplayName,
    string? Notes);
