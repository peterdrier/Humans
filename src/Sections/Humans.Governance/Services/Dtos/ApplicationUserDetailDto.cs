using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

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
    IReadOnlyList<ApplicationStateHistoryDto> History)
{
    /// <summary>Only a Submitted application can be withdrawn — the rule <c>WithdrawAsync</c> enforces.</summary>
    public bool CanWithdraw => Status == ApplicationStatus.Submitted;
}
