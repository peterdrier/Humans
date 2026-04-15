using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.Governance;

/// <summary>
/// Detail projection for a user's own application detail view
/// (<c>Application/Details.cshtml</c>). Does not include the applicant's own
/// user/profile fields because the controller already knows who they are.
/// </summary>
public record ApplicationUserDetailDto(
    Guid Id,
    Guid UserId,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    string Motivation,
    string? AdditionalInfo,
    string? SignificantContribution,
    string? RoleUnderstanding,
    Instant SubmittedAt,
    Instant? ReviewStartedAt,
    Instant? ResolvedAt,
    string? ReviewerName,
    string? ReviewNotes,
    IReadOnlyList<ApplicationStateHistoryDto> History);
