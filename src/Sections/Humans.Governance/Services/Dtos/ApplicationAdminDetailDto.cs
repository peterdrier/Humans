using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

internal sealed record ApplicationAdminDetailDto(
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
    IReadOnlyList<ApplicationStateHistoryDto> History)
{
    /// <summary>Only a Submitted application can be approved or rejected — the status rule
    /// <c>ApproveAsync</c>/<c>RejectAsync</c> enforce (they also refuse it with no votes cast).</summary>
    public bool CanApproveReject => Status == ApplicationStatus.Submitted;
}
