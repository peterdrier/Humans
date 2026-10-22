using NodaTime;

namespace Humans.Users.Contracts;

public interface IUserEmailService
{
    /// <summary>In display order.</summary>
    Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the verification resend cooldown has elapsed for a pending row.</summary>
    Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default);

    /// <summary>Filtered by the viewer's access level.</summary>
    Task<IReadOnlyList<UserEmailDto>> GetVisibleEmailsAsync(
        Guid userId,
        ContactFieldVisibility accessLevel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the verification token and whether the address belongs to another account (verifying then opens a
    /// merge request), or an ErrorKey. A refusal creates no row and no token.
    /// </summary>
    Task<AddEmailResult> AddEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="emailId"/> is the pending row the link was issued for: the token is bound to it, which
    /// disambiguates several pending rows. An address verified on another account opens a merge request instead.
    /// Dependency faults propagate.
    /// </summary>
    Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId,
        Guid emailId,
        string token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a pending plain row without a token, when the admin confirmed ownership another way. Same
    /// duplicate branch as <see cref="VerifyEmailAsync"/>; audited to <paramref name="actorUserId"/>. Throws
    /// <see cref="System.ComponentModel.DataAnnotations.ValidationException"/> when the row is missing, already
    /// verified or provider-attached (those verify through the OAuth callback).
    /// </summary>
    Task<VerifyEmailResult> AdminMarkVerifiedAsync(
        Guid userId,
        Guid emailId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the notification target. Refuses a missing or unverified row.</summary>
    Task<UpdateEmailResult> SetPrimaryAsync(
        Guid userId,
        Guid emailId,
        CancellationToken cancellationToken = default);

    /// <summary>Refuses a missing row.</summary>
    Task<UpdateEmailResult> SetVisibilityAsync(
        Guid userId,
        Guid emailId,
        ContactFieldVisibility? visibility,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Provider-attached rows must go through <see cref="UnlinkAsync"/>. Also refuses the primary row, the last
    /// verified row, and a row whose address is linked to the user's event ticket (a vendor re-sync could
    /// disconnect the ticket).
    /// </summary>
    Task<DeleteEmailResult> DeleteEmailAsync(
        Guid userId,
        Guid emailId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// No verification flow. A @nobodies.team address becomes the notification target. Returns false when the
    /// address already exists for this user.
    /// </summary>
    Task<bool> AddVerifiedEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// For freshly provisioned accounts, so the row goes through the same primary invariant and cache invalidation
    /// as every other add. Skips an address the user already has.
    /// </summary>
    Task AddProvisionedEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every row across all users matching <paramref name="email"/>; <paramref name="aliased"/> adds the
    /// gmail/googlemail twin. A caller that mutates state must treat more than one distinct owner as ambiguous:
    /// verified-address uniqueness can drift. Served from the <c>UserInfo</c> cache.
    /// </summary>
    Task<IReadOnlyList<UserEmailRowSnapshot>> FindByAddressAsync(
        string email,
        bool aliased,
        bool verifiedOnly,
        CancellationToken cancellationToken = default);

    /// <summary>Null if none.</summary>
    Task<string?> GetNobodiesTeamEmailAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The verified primary row, else <c>User.Email</c>; users with neither are absent. Keyed by the requested id,
    /// with the resolved human's address: a merged-away id gets the survivor's.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetUserIdsByEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken cancellationToken = default);

    /// <summary>The verified primary row, else <c>User.Email</c>; null when neither exists.</summary>
    Task<string?> GetPrimaryEmailAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetVerifiedEmailsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Empty when the user has no emails.</summary>
    Task<IReadOnlyList<UserEmailRowSnapshot>> GetEntitiesByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Users with no notification target are absent.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive.</summary>
    Task<IReadOnlyList<UserEmailMatch>> MatchByEmailsAsync(
        IReadOnlyCollection<string> emails,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin recovery: clears the flag without promoting a sibling, leaving the user without a primary until
    /// <see cref="SetPrimaryAsync"/> or the next mutating path. False when not found or already cleared.
    /// </summary>
    Task<bool> ClearPrimaryAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Users whose verified rows don't have exactly one primary.</summary>
    Task<IReadOnlyList<UserEmailFlagViolation>> GetEmailFlagViolationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Rows whose user is missing or a merge tombstone.</summary>
    Task<IReadOnlyList<UserEmailOrphan>> GetOrphanUserEmailsAsync(CancellationToken ct = default);

    /// <summary>False if the row no longer exists.</summary>
    Task<bool> DeleteByIdAsync(Guid emailId, CancellationToken ct = default);

    /// <summary>
    /// Removes the login row and the email row of a provider-attached address. Refuses when it would leave
    /// <paramref name="userId"/> (the target, not <paramref name="actorUserId"/>) with no verified email: magic
    /// link needs one too, so that would be a full lockout.
    /// </summary>
    Task<UnlinkEmailResult> UnlinkAsync(
        Guid userId,
        Guid userEmailId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The one OAuth-callback path that mutates email rows, after the login row points at <paramref name="userId"/>.
    /// In one transaction:
    /// <list type="number">
    /// <item>Tagged row at <paramref name="claimEmail"/> → <see cref="ReconcileOutcome.NoChange"/>.</item>
    /// <item>Another row of this user holds the claim email → tag-move (union <c>IsPrimary</c>, force verify, delete
    /// the old row) → <see cref="ReconcileOutcome.TagMoved"/>.</item>
    /// <item>Tagged row elsewhere, no row holds the claim email → rewrite → <see cref="ReconcileOutcome.EmailRewritten"/>.</item>
    /// <item>No tagged row and no row holds it → insert a verified tagged row → <see cref="ReconcileOutcome.NewRowCreated"/>.</item>
    /// </list>
    /// If another user already verified-holds the claim email, a verified claim displaces their row
    /// (<see cref="ReconcileOutcome.CrossUserDisplaced"/>) and an unverified one changes nothing
    /// (<see cref="ReconcileOutcome.CrossUserBlocked"/>). This writes every audit row itself, so callers must not.
    /// Sign-in must never block on it: the caller swallows its exceptions.
    /// </summary>
    Task<OAuthReconcileResult> ReconcileOAuthIdentityAsync(
        Guid userId,
        string provider,
        string providerKey,
        string claimEmail,
        bool claimEmailVerified,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The verified-email unique index caught a concurrent cross-user collision past the pre-check. A domain type so
/// Web never imports Npgsql.
/// </summary>
public sealed class OAuthReconcileConcurrencyException : Exception
{
    public OAuthReconcileConcurrencyException() { }
    public OAuthReconcileConcurrencyException(string message) : base(message) { }
    public OAuthReconcileConcurrencyException(string message, Exception inner)
        : base(message, inner) { }
}

public enum ReconcileOutcome
{
    NoChange,
    EmailRewritten,
    /// <summary>Flags unioned, old row deleted.</summary>
    TagMoved,
    NewRowCreated,
    /// <summary>The other user's row was deleted because the provider asserted verification.</summary>
    CrossUserDisplaced,
    /// <summary>The provider didn't assert verification. Nothing changed.</summary>
    CrossUserBlocked,
}

/// <summary>Informational: the service writes the audit rows itself.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="PreviousEmail">The tagged row's old address, for EmailRewritten or TagMoved; null otherwise.</param>
/// <param name="AffectedRowId">The signing user's row now carrying the tag; null for CrossUserBlocked.</param>
/// <param name="DisplacedUserId">The other user, for CrossUserDisplaced or CrossUserBlocked.</param>
/// <param name="DisplacedRowId">The deleted row (CrossUserDisplaced) or the blocking row (CrossUserBlocked).</param>
/// <param name="DisplacedEmail">The address on that row.</param>
/// <param name="DisplacedUserLeftWithoutVerifiedEmail">The displacement deleted the other user's last verified row.</param>
public record OAuthReconcileResult(
    ReconcileOutcome Outcome,
    string? PreviousEmail,
    Guid? AffectedRowId,
    Guid? DisplacedUserId,
    Guid? DisplacedRowId,
    string? DisplacedEmail,
    bool DisplacedUserLeftWithoutVerifiedEmail);

public record UserEmailMatch(
    string Email,
    Guid UserId,
    bool IsPrimary,
    bool IsVerified,
    Instant UpdatedAt);

/// <summary>For the admin remediation screen.</summary>
/// <param name="UserId">The user.</param>
/// <param name="VerifiedCount">Verified rows.</param>
/// <param name="VerifiedPrimaryCount">Verified primary rows; healthy is 1, or 0 with no verified rows.</param>
/// <param name="HasPrimaryProblem">Verified rows exist and <see cref="VerifiedPrimaryCount"/> isn't 1.</param>
public record UserEmailFlagViolation(
    Guid UserId,
    int VerifiedCount,
    int VerifiedPrimaryCount,
    bool HasPrimaryProblem);

public record UserEmailOrphan(
    Guid UserId,
    Guid EmailId,
    string Email);

public sealed record UserEmailRowSnapshot(
    Guid Id,
    Guid UserId,
    string Email,
    bool IsVerified,
    string? Provider,
    string? ProviderKey,
    bool IsPrimary,
    ContactFieldVisibility? Visibility,
    Instant? VerificationSentAt,
    Instant CreatedAt,
    Instant UpdatedAt);
