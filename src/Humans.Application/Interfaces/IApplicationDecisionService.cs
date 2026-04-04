using Humans.Domain.Enums;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces;

/// <summary>
/// Single code path for tier application lifecycle: submit, withdraw, approve, reject.
/// Handles state transitions, term expiry, profile tier update, audit log,
/// GDPR vote cleanup, team sync, and notification email.
/// </summary>
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

    Task<IReadOnlyList<MemberApplication>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default);

    Task<MemberApplication?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default);

    Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default);

    Task<ApplicationDecisionResult> WithdrawAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default);

    Task<(IReadOnlyList<MemberApplication> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default);

    Task<MemberApplication?> GetApplicationDetailAsync(Guid applicationId, CancellationToken ct = default);
}

public record ApplicationDecisionResult(bool Success, string? ErrorKey = null, Guid? ApplicationId = null);
