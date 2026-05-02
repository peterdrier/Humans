using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Profiles;

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
    Task SetPrimaryAsync(
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
    /// Deletes a UserEmail row. Returns <c>true</c> when the row was removed,
    /// <c>false</c> when the precondition rejected the delete (currently:
    /// rows with a non-empty <see cref="UserEmail.Provider"/> must go through
    /// <see cref="UnlinkAsync"/>, which removes the AspNetUserLogins row and
    /// the UserEmail row in one step). Blocks the delete (throws
    /// <see cref="System.ComponentModel.DataAnnotations.ValidationException"/>)
    /// when removing a verified row would leave the user with zero verified
    /// UserEmail rows. Unverified rows are always deletable since they can't
    /// be used for sign-in. See
    /// <c>docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md</c>
    /// PRs 1 and 4 for the design rationale.
    /// </summary>
    Task<bool> DeleteEmailAsync(
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
    /// Resolves the notification-target email for each requested user. The
    /// result is <c>UserEmail.Email</c> where <c>IsPrimary</c> is
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
    /// enqueue path to stamp <see cref="Domain.Entities.EmailOutboxMessage.UserId"/>
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
    /// Returns true when any <see cref="Domain.Entities.UserEmail"/> row
    /// exists whose <c>Email</c> matches <paramref name="email"/> case-insensitively,
    /// irrespective of user. Used by admin account-linking flows to reject duplicate
    /// links before mutating state.
    /// </summary>
    Task<bool> IsEmailLinkedToAnyUserAsync(
        string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites the user's <see cref="Domain.Entities.UserEmail"/> row
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

    /// <summary>
    /// Sets the user's canonical Google Workspace identity to the given verified
    /// email row. Single-transaction exclusive flip via
    /// <see cref="IUserEmailRepository.SetGoogleExclusiveAsync"/>: the target row's
    /// <see cref="UserEmail.IsGoogle"/> goes to true, every sibling row for the
    /// same user is cleared. Owner-gated via
    /// <see cref="IUserEmailRepository.GetByIdAndUserIdAsync"/>; returns
    /// <c>false</c> if the row is not found for this user or is not verified.
    /// Service-auth-free per the design rules: the controller authorizes against
    /// <paramref name="userId"/>, which is the <b>target</b> user (not the actor).
    /// <paramref name="actorUserId"/> is captured on the audit log entry so the
    /// admin self/admin grid distinguishes who flipped the flag.
    /// </summary>
    Task<bool> SetGoogleAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find-or-create. Attaches the OAuth identity (<paramref name="provider"/>,
    /// <paramref name="providerKey"/>) to the user's email row matching
    /// <paramref name="email"/> (Ordinal/case-insensitive); creates a new
    /// verified row when none matches. <paramref name="userId"/> is the
    /// <b>target</b> user; <paramref name="actorUserId"/> is the actor.
    /// Replaces the legacy AddOAuthEmailAsync + SetProviderAsync pair (PR 4
    /// consolidation).
    /// </summary>
    Task<bool> LinkAsync(
        Guid userId,
        string provider,
        string providerKey,
        string email,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes both the AspNetUserLogins row and the UserEmail row for a
    /// Provider-attached email. Owner-gated. Returns <c>false</c> if the row
    /// is not found for this user or has no <see cref="UserEmail.Provider"/>/
    /// <see cref="UserEmail.ProviderKey"/>. No "would lock yourself out"
    /// guard — magic-link sign-in is the fallback. <paramref name="userId"/>
    /// is the <b>target</b> user; <paramref name="actorUserId"/> is the actor.
    /// </summary>
    Task<bool> UnlinkAsync(
        Guid userId,
        Guid userEmailId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the UserEmail row tagged with
    /// <paramref name="provider"/> / <paramref name="providerKey"/>. Returns
    /// <c>null</c> when no row matches. Used by the OAuth callback's rename
    /// detection to compare the row's email against the incoming claim email
    /// and update the row when they diverge.
    /// </summary>
    Task<UserEmailProviderMatch?> FindByProviderKeyAsync(
        string provider, string providerKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Narrow projection of a UserEmail row matched by (Provider, ProviderKey).
/// Returned from <see cref="IUserEmailService.FindByProviderKeyAsync"/> so the
/// service interface does not leak the Domain entity into Web-layer callers.
/// </summary>
public record UserEmailProviderMatch(Guid Id, Guid UserId, string Email);

/// <summary>
/// Narrow projection describing a <see cref="Domain.Entities.UserEmail"/>
/// row used by admin cross-section matching. Avoids leaking the full entity
/// outside the owning section.
/// </summary>
public record UserEmailMatch(
    string Email,
    Guid UserId,
    bool IsPrimary,
    bool IsVerified,
    Instant UpdatedAt);
