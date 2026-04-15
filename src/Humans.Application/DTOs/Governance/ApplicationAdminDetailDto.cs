using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.Governance;

/// <summary>
/// Detail projection for the admin application detail view
/// (<c>ApplicationDetail.cshtml</c>). All user-related fields are stitched
/// from <c>IUserService</c> so the Application entity has no cross-domain
/// navigation properties.
/// </summary>
public record ApplicationAdminDetailDto(
    Guid Id,
    Guid UserId,
    string UserEmail,
    string UserDisplayName,
    string? UserProfilePictureUrl,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    string Motivation,
    string? AdditionalInfo,
    string? SignificantContribution,
    string? RoleUnderstanding,
    string? Language,
    Instant SubmittedAt,
    Instant? ReviewStartedAt,
    Instant? ResolvedAt,
    string? ReviewerName,
    string? ReviewNotes,
    IReadOnlyList<ApplicationStateHistoryDto> History);
