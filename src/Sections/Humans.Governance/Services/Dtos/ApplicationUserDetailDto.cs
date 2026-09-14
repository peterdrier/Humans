using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Detail projection for a user's own application detail view
/// (<c>Views/Governance/Applications/Details.cshtml</c>). Omits the applicant's
/// own user/profile fields — the controller already knows who they are.
/// </summary>
internal sealed record ApplicationUserDetailDto(
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
