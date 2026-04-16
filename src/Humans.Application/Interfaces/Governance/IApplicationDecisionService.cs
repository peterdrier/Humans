using Humans.Application.DTOs.Governance;
using Humans.Domain.Enums;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Governance;

/// <summary>
/// Single code path for tier application lifecycle: submit, withdraw, approve, reject.
/// Handles state transitions, term expiry, profile tier update, audit log,
/// GDPR vote cleanup, team sync, and notification email.
/// </summary>
/// <remarks>
/// After the Governance repo/store/decorator migration, three read methods
/// return stitched DTOs instead of <see cref="MemberApplication"/> entities:
/// detail and filtered-list shapes now carry user/reviewer display info
/// resolved via <c>IUserService</c>, because the entity no longer carries
/// cross-domain navigation properties (design-rules §6).
/// </remarks>
public interface IApplicationDecisionService
{
    Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default);

    Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reason,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a user's own applications, ordered by <c>SubmittedAt</c> desc.
    /// Callers only read scalar fields (Status, MembershipTier, SubmittedAt,
    /// ResolvedAt) so the entity shape is preserved.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a user's own application detail with reviewer display name
    /// stitched from <c>IUserService</c>. Null if the application does not
    /// belong to the user or does not exist.
    /// </summary>
    Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default);

    Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default);

    Task<ApplicationDecisionResult> WithdrawAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Paginated admin list of applications with applicant user display
    /// fields stitched in. Defaults to <see cref="ApplicationStatus.Submitted"/>
    /// when <paramref name="statusFilter"/> is null/empty/unrecognized.
    /// </summary>
    Task<(IReadOnlyList<ApplicationAdminRowDto> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Admin detail view for a single application with user + reviewer
    /// display fields stitched from <c>IUserService</c>.
    /// </summary>
    Task<ApplicationAdminDetailDto?> GetApplicationDetailAsync(
        Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Updates a submitted (draft) application's motivation, tier, and optional
    /// Asociado fields. Only allowed on <see cref="ApplicationStatus.Submitted"/>
    /// applications. Used by the profile save flow during initial setup.
    /// </summary>
    Task UpdateDraftApplicationAsync(
        Guid applicationId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        CancellationToken ct = default);
}

public record ApplicationDecisionResult(bool Success, string? ErrorKey = null, Guid? ApplicationId = null);
