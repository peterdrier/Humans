using NodaTime;
using Profiles.Domain.Enums;

namespace Profiles.Application.DTOs;

/// <summary>
/// Summary of a member for listing purposes.
/// </summary>
public record MemberSummary(
    Guid UserId,
    string DisplayName,
    string Email,
    MembershipStatus Status,
    IReadOnlyList<string> Roles,
    Instant CreatedAt,
    Instant? LastLoginAt);
