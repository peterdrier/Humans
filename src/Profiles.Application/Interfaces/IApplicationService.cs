using Profiles.Domain.Enums;
using MemberApplication = Profiles.Domain.Entities.Application;

namespace Profiles.Application.Interfaces;

/// <summary>
/// Service for managing membership applications.
/// </summary>
public interface IApplicationService
{
    /// <summary>
    /// Submits a new application.
    /// </summary>
    /// <param name="userId">The applicant user ID.</param>
    /// <param name="motivation">The motivation statement.</param>
    /// <param name="additionalInfo">Optional additional information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created application.</returns>
    Task<MemberApplication> SubmitApplicationAsync(
        Guid userId,
        string motivation,
        string? additionalInfo = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an application by ID.
    /// </summary>
    /// <param name="applicationId">The application ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application if found.</returns>
    Task<MemberApplication?> GetByIdAsync(Guid applicationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all applications for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All applications for the user.</returns>
    Task<IReadOnlyList<MemberApplication>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets applications by status.
    /// </summary>
    /// <param name="status">The status to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Applications with the specified status.</returns>
    Task<IReadOnlyList<MemberApplication>> GetByStatusAsync(ApplicationStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts reviewing an application.
    /// </summary>
    /// <param name="applicationId">The application ID.</param>
    /// <param name="reviewerUserId">The reviewer user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StartReviewAsync(Guid applicationId, Guid reviewerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves an application.
    /// </summary>
    /// <param name="applicationId">The application ID.</param>
    /// <param name="reviewerUserId">The reviewer user ID.</param>
    /// <param name="notes">Optional notes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApproveAsync(Guid applicationId, Guid reviewerUserId, string? notes = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects an application.
    /// </summary>
    /// <param name="applicationId">The application ID.</param>
    /// <param name="reviewerUserId">The reviewer user ID.</param>
    /// <param name="reason">The reason for rejection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RejectAsync(Guid applicationId, Guid reviewerUserId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws an application.
    /// </summary>
    /// <param name="applicationId">The application ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WithdrawAsync(Guid applicationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests more information from an applicant.
    /// </summary>
    /// <param name="applicationId">The application ID.</param>
    /// <param name="reviewerUserId">The reviewer user ID.</param>
    /// <param name="notes">What information is needed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequestMoreInfoAsync(Guid applicationId, Guid reviewerUserId, string notes, CancellationToken cancellationToken = default);
}
