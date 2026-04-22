using Humans.Application.DTOs;
using Humans.Domain.Enums;
using NodaTime;

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
    /// Adds an OAuth-sourced email (already verified, no token needed).
    /// Used during first-time OAuth login to record the provider email.
    /// </summary>
    Task AddOAuthEmailAsync(
        Guid userId,
        string email,
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

    /// Resolves the notification-target email for each requested user. The
    /// result is <c>UserEmail.Email</c> where <c>IsNotificationTarget</c> is
    /// true and the email is verified, falling back to <c>User.Email</c> when
    /// no notification-target email exists. Users for whom no email can be
    /// resolved are omitted from the result. Used by cross-section callers
    /// (Feedback, Campaigns, future mass-mail pipelines) so they never navigate
    /// <c>User.UserEmails</c> directly.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the owning user for a verified email address. Exact match on
    /// <see cref="UserEmail.Email"/> — no gmail/googlemail aliasing. Returns
    /// <c>null</c> if no verified row matches. Used by the email-outbox
    /// enqueue path to stamp <see cref="Humans.Domain.Entities.EmailOutboxMessage.UserId"/>
    /// so admin views and unsubscribe flows can tie the row back to the human.
    /// </summary>
    Task<Guid?> GetUserIdByVerifiedEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every verified email address belonging to the user. Used by
    /// cross-section callers (Tickets, matching) that need to compare incoming
    /// addresses against a user's verified set without reading
    /// <c>user_emails</c> directly.
    /// </summary>
    Task<IReadOnlyList<string>> GetVerifiedEmailsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the notification-target email for each of the given user ids
    /// (batch query). Users with no notification target are absent from the
    /// returned dictionary. Used by cross-section callers (Tickets "who
    /// hasn't bought" admin list) that need to resolve display emails in
    /// memory without reading <c>user_emails</c> directly.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns distinct user ids whose verified email set contains the given
    /// case-insensitive substring. Used by admin search surfaces (Tickets
    /// "who hasn't bought") so secondary verified addresses are discoverable
    /// even when they differ from the notification-target email.
    /// </summary>
    Task<IReadOnlyList<Guid>> SearchUserIdsByVerifiedEmailAsync(
        string searchTerm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the id of any user, other than <paramref name="excludeUserId"/>,
    /// whose user_emails rows contain the given address (case-insensitive), or
    /// null if no other user owns it. Used by @nobodies.team provisioning so
    /// the Application-layer service can detect cross-user conflicts without
    /// touching the database directly.
    /// </summary>
    Task<Guid?> GetOtherUserIdHavingEmailAsync(
        string email,
        Guid excludeUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true when any <see cref="Humans.Domain.Entities.UserEmail"/> row
    /// exists whose <c>Email</c> matches <paramref name="email"/> case-insensitively,
    /// irrespective of user. Used by admin account-linking flows to reject duplicate
    /// links before mutating state.
    /// </summary>
    Task<bool> IsEmailLinkedToAnyUserAsync(
        string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites the user's <see cref="Humans.Domain.Entities.UserEmail"/> row
    /// whose address matches <paramref name="oldEmail"/> (case-insensitive) to
    /// <paramref name="newEmail"/> and stamps <c>UpdatedAt</c>. Used by the
    /// admin rename-fix flow. No-op if the user has no matching row.
    /// </summary>
    Task RewriteEmailAddressAsync(
        Guid userId, string oldEmail, string newEmail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every <see cref="UserEmailMatch"/> whose address matches one of
    /// <paramref name="emails"/> (case-insensitive). Used by the Google admin
    /// workspace-accounts list to match Google-side accounts to humans without
    /// loading the full <c>user_emails</c> table.
    /// </summary>
    Task<IReadOnlyList<UserEmailMatch>> MatchByEmailsAsync(
        IReadOnlyCollection<string> emails,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Narrow projection describing a <see cref="Humans.Domain.Entities.UserEmail"/>
/// row used by admin cross-section matching. Avoids leaking the full entity
/// outside the owning section.
/// </summary>
public record UserEmailMatch(
    string Email,
    Guid UserId,
    bool IsNotificationTarget,
    bool IsVerified,
    Instant UpdatedAt);
