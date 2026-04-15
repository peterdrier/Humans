using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.Governance;

/// <summary>
/// Stitched view of a single <c>ApplicationStateHistory</c> row with the
/// actor's display name resolved via <c>IUserService</c> at the service layer.
/// </summary>
public record ApplicationStateHistoryDto(
    ApplicationStatus Status,
    Instant ChangedAt,
    Guid ChangedByUserId,
    string? ChangedByDisplayName,
    string? Notes);
