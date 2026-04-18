using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for managing user email addresses.
/// </summary>
public interface IUserEmailService
{
    /// <summary>
    /// Gets all emails for a user, ordered by display order.
    /// </summary>
    Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets emails visible on a user's profile based on viewer access level.
    /// </summary>
    Task<IReadOnlyList<UserEmailDto>> GetVisibleEmailsAsync(
        Guid userId,
        ContactFieldVisibility accessLevel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new email address and initiates verification.
    /// Returns a result containing the verification token and whether the email conflicts
    /// with another account (which will trigger a merge request on verification).
    /// </summary>
    Task<AddEmailResult> AddEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies an email address using a token.
    /// If the email is already verified on another account, creates a merge request
    /// instead of completing verification.
    /// Returns a result indicating the email and whether a merge request was created.
    /// </summary>
    Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId,
        string token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets which email is the notification target.
    /// The email must be verified.
    /// </summary>
    Task SetNotificationTargetAsync(
        Guid userId,
        Guid emailId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the profile visibility for an email.
    /// </summary>
    Task SetVisibilityAsync(
        Guid userId,
        Guid emailId,
        ContactFieldVisibility? visibility,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a non-OAuth email address.
    /// </summary>
    Task DeleteEmailAsync(
        Guid userId,
        Guid emailId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all email records for a user (used during account anonymization).
    /// </summary>
    Task RemoveAllEmailsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an unverified email record by id. No-op if the email does not
    /// exist or has been verified. Narrower than <see cref="DeleteEmailAsync"/>
    /// — does not block OAuth emails (which are always verified, so cannot
    /// reach this path) and does not reassign notification target (pending
    /// emails cannot be notification targets). Used by the account-merge
    /// rejection flow.
    /// </summary>
    Task RemoveUnverifiedEmailAsync(
        Guid emailId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all verified email addresses for a user. Used by consumers that
    /// need to match a user against attendee records or similar external
    /// verification flows (e.g., ticket attendee email matching).
    /// </summary>
    Task<IReadOnlyList<string>> GetVerifiedEmailAddressesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all email addresses (any verification state) for the given
    /// users, keyed by user id. Used by admin search flows that need to match
    /// a search term against any of a user's emails. Users with no emails are
    /// absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetAllEmailsByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an OAuth-sourced email (already verified, no token needed).
    /// Used during first-time OAuth login to record the provider email.
    /// </summary>
    Task AddOAuthEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the email address of the OAuth-sourced UserEmail record
    /// for the given user, if one exists. No-op if the user has no OAuth
    /// UserEmail record.
    /// </summary>
    Task UpdateOAuthEmailAsync(
        Guid userId,
        string newEmail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the email address of the UserEmail record matching
    /// <paramref name="oldEmail"/> for the given user (case-insensitive
    /// match). Also refreshes <c>UpdatedAt</c>. No-op if no matching
    /// record exists.
    /// </summary>
    Task UpdateUserEmailAddressAsync(
        Guid userId,
        string oldEmail,
        string newEmail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a verified email directly (admin provisioning/linking — no verification flow needed).
    /// If the email is @nobodies.team, it's automatically set as the notification target.
    /// Skips if the email already exists for this user.
    /// </summary>
    Task AddVerifiedEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// If the user has a verified @nobodies.team email but GoogleEmail is null, sets it.
    /// Returns true if GoogleEmail was updated.
    /// </summary>
    Task<bool> TryBackfillGoogleEmailAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the verified @nobodies.team email for a user, or null if none exists.
    /// </summary>
    Task<string?> GetNobodiesTeamEmailAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has a verified @nobodies.team email.
    /// </summary>
    Task<bool> HasNobodiesTeamEmailAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the email address for a verified email record owned by the user.
    /// Returns null if not found, not owned by the user, or not verified.
    /// </summary>
    Task<string?> GetVerifiedEmailAddressAsync(
        Guid userId,
        Guid emailId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a verified UserEmail matching the given address (or gmail/googlemail alternate).
    /// Includes the owning User for contact-creation conflict checks.
    /// Returns null if no match.
    /// </summary>
    Task<UserEmailWithUser?> FindVerifiedEmailWithUserAsync(
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets @nobodies.team email status for all users who have one.
    /// Returns a dictionary of userId → isNotificationTarget (i.e., is it their primary email).
    /// Used for admin listing pages.
    /// </summary>
    Task<Dictionary<Guid, bool>> GetNobodiesTeamEmailStatusByUserAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the verified @nobodies.team email for each of the given users (batch query).
    /// Returns a dictionary of userId → email address. Users without a @nobodies.team email are omitted.
    /// </summary>
    Task<Dictionary<Guid, string>> GetNobodiesTeamEmailsByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the effective notification email for a user: the verified
    /// notification-target email if one exists, otherwise the user's primary
    /// <c>User.Email</c>. Returns <c>null</c> if the user does not exist or
    /// has no email at all.
    /// </summary>
    Task<string?> GetNotificationEmailAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch equivalent of <see cref="GetNotificationEmailAsync"/>. Returns
    /// a dictionary of <c>userId</c> → effective notification email for the
    /// given user ids. Users not found, or found but without any email at
    /// all, are absent from the returned dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);
}
