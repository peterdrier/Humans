using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

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
