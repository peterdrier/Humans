using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Service for managing user email addresses.
/// </summary>
public interface IUserEmailService : IApplicationService
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
    /// Verifies an email address using a token. <paramref name="emailId"/>
    /// identifies the specific pending row the verification link was issued
    /// for — the token is bound to this row's Id via the token's purpose
    /// suffix, so passing it here disambiguates when the same user has
    /// multiple pending plain rows (issue nobodies-collective/Humans#611).
    /// If the email is already verified on another account, creates a merge
    /// request instead of completing verification. Returns a result
    /// indicating the email and whether a merge request was created.
    /// </summary>
    Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId,
        Guid emailId,
        string token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin-only: marks a pending plain (non-OAuth) UserEmail row verified
    /// without consuming a verification token. Used when the user can't
    /// complete the token flow themselves (mailbox unreachable, lost link,
    /// expired link) but the admin has confirmed ownership through other
    /// means. Mirrors <see cref="VerifyEmailAsync"/>'s duplicate-email
    /// branch — if the address is already verified on another account, a
    /// merge request is created instead of completing verification.
    /// Writes an audit entry attributing the action to
    /// <paramref name="actorUserId"/>. Throws
    /// <see cref="System.ComponentModel.DataAnnotations.ValidationException"/>
    /// when the row is not found, already verified, or has a non-null
    /// <see cref="UserEmail.Provider"/> (provider-attached rows are verified
    /// through the OAuth callback, not this path).
    /// </summary>
    Task<VerifyEmailResult> AdminMarkVerifiedAsync(
        Guid userId,
        Guid emailId,
        Guid actorUserId,
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
    /// Idempotent: if the email already exists for this user, skips the insert
    /// and returns <c>false</c>. Returns <c>true</c> when a row was actually inserted.
    /// </summary>
    Task<bool> AddVerifiedEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotent verified-email add for the import-flow account-creation path
    /// (<c>AccountProvisioningService</c>): on a freshly created User the row
    /// is added through <see cref="UserEmailService"/> rather than the
    /// repository directly so it goes through the same orchestrator
    /// (Primary + Google invariants, UserInfo invalidation) as every
    /// other UserEmail-add path. Issue nobodies-collective/Humans#687.
    /// Skips if the email already exists for this user (idempotent).
    /// </summary>
    Task AddProvisionedEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the owning userId for any UserEmail row matching the given
    /// address (case-insensitive normalized, including the gmail/googlemail
    /// alternate). Returns the userId of any matching row regardless of
    /// verification state — used by import-flow account provisioning to
    /// detect existing accounts before creating a new one. Returns null when
    /// no row matches. Orphan detection (matched row pointing at a missing
    /// user) is the caller's responsibility — see
    /// <see cref="Humans.Application.Services.Users.AccountProvisioningService"/>.
    /// </summary>
    Task<Guid?> FindAnyUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns (userId, userEmailId) for any UserEmail row matching the address,
    /// or null if no row matches. Used by Mailer import to identify the specific
    /// unverified row to delete before creating a contact.
    /// </summary>
    Task<(Guid UserId, Guid EmailId)?> FindAnyEmailRowByAddressAsync(
        string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Legacy backfill that wrote the verified @nobodies.team email into the
    /// <c>User.GoogleEmail</c> shadow column. With the column deprecated and
    /// the new <c>EnsureGoogleInvariantAsync</c> running on every UserEmail
    /// row creation, the shadow column no longer participates in the Google
    /// identity invariant. Method body is now a no-op kept temporarily so
    /// tests and callers compile through the obsolete transition.
    /// </summary>
    [Obsolete("Issue nobodies-collective/Humans#687: User.GoogleEmail is being deprecated. UserEmailService.EnsureGoogleInvariantAsync now stamps IsGoogle on the canonical row whenever a UserEmail is added; no separate backfill is needed.")]
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
    /// Returns the distinct UserIds whose verified UserEmail matches the given
    /// address (including gmail/googlemail alternate). Same matching semantics
    /// as <see cref="FindVerifiedEmailWithUserAsync"/> but exposes the full
    /// set. Callers that mutate user state (e.g. the Mailer import classifier)
    /// must treat count &gt; 1 as ambiguous and skip — service-enforced
    /// verified-email uniqueness can drift.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetDistinctVerifiedUserIdsAsync(
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
    /// Returns distinct user ids whose email rows match the supplied prefix
    /// and suffix. Used by Admin-only maintenance flows that resolve marked
    /// account sets through the Profile-owned email table.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsByEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken cancellationToken = default);

    /// <summary>Resolve a user by exact, case-insensitive email match against UserEmails. Returns null if zero or ambiguous matches.</summary>
    Task<Guid?> GetUserIdByExactEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Returns the notification-target (IsPrimary=true, verified) email address for the user,
    /// falling back to <c>User.Email</c> when no primary row exists. Returns <c>null</c> when
    /// no email can be resolved. Used by transfer-request creation to snapshot the recipient's
    /// preferred contact address.
    /// </summary>
    Task<string?> GetPrimaryEmailAsync(Guid userId, CancellationToken ct = default);

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
    /// Returns every <see cref="UserEmail"/> entity belonging to
    /// <paramref name="userId"/>, including the per-row metadata
    /// (<c>IsVerified</c>, <c>IsPrimary</c>, <c>IsGoogle</c>,
    /// <c>Provider</c>, etc.). Used by cross-section callers
    /// (GoogleAdmin / GoogleWorkspaceSync) that need the row-level metadata
    /// to compute Google-rename detection or sync flags. Returns an empty
    /// list when the user has no emails. The return type is the raw
    /// entity rather than a DTO because the callers genuinely need the
    /// per-row flags — projecting to a DTO would just rename them.
    /// </summary>
    Task<IReadOnlyList<UserEmailRowSnapshot>> GetEntitiesByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every <see cref="UserEmail"/> grouped by <c>UserId</c>. Used
    /// by cross-section callers that need to bulk-load row-level metadata
    /// for a known set of users (e.g. the Google sync hot path that calls
    /// this once per reconcile). Users with no emails are absent from the
    /// returned dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>> GetEntitiesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
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
    /// Clears <see cref="UserEmail.IsGoogle"/> on a single row without promoting
    /// any sibling — admin recovery path for the data-corruption case where
    /// multiple rows have <c>IsGoogle = true</c> for the same user (an
    /// invariant violation that bypasses <see cref="SetGoogleAsync"/>).
    /// Returns <c>false</c> when the row is not found for this user or is
    /// already cleared. Owner-gated via
    /// <see cref="IUserEmailRepository.GetByIdAndUserIdAsync"/>.
    /// </summary>
    Task<bool> ClearGoogleAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears <see cref="UserEmail.IsPrimary"/> on a single row without
    /// promoting any sibling — admin recovery path for cases where multiple
    /// rows are flagged primary or where the admin needs to deliberately
    /// pick a new primary after dropping the current one. Leaves the user
    /// in the temporarily-no-primary state; the admin is expected to call
    /// <see cref="SetPrimaryAsync"/> afterward (or
    /// <c>EnsurePrimaryInvariantAsync</c> will fire on the next mutating
    /// path). Returns <c>false</c> when the row is not found for this user
    /// or is already cleared.
    /// </summary>
    Task<bool> ClearPrimaryAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one entry per user whose UserEmail rows currently violate the
    /// admin-visible flag invariants:
    /// <list type="bullet">
    /// <item>more than one row with <c>IsGoogle = true</c>; or</item>
    /// <item>verified rows present but the count of <c>IsPrimary = true</c>
    /// verified rows is not exactly 1.</item>
    /// </list>
    /// Used by the Google admin "email flag violations" remediation screen.
    /// </summary>
    Task<IReadOnlyList<UserEmailFlagViolation>> GetEmailFlagViolationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns UserEmail rows whose UserId points to a non-existent or tombstoned
    /// User (User row absent OR <c>MergedToUserId</c> set). Used by the EmailProblems
    /// admin scan. At ~500 users, full-table scan is trivial.
    /// </summary>
    Task<IReadOnlyList<UserEmailOrphan>> GetOrphanUserEmailsAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes a single UserEmail row by id. Used by EmailProblems orphan cleanup.
    /// Idempotent — returns false if the row no longer exists.
    /// </summary>
    Task<bool> DeleteByIdAsync(Guid emailId, CancellationToken ct = default);

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
    /// Issue nobodies-collective/Humans#697. The single OAuth-callback entry
    /// point that mutates <see cref="Domain.Entities.UserEmail"/> rows. Called
    /// once per OAuth-success path in <c>AccountController</c> after Identity's
    /// <c>AspNetUserLogins</c> row points at <paramref name="userId"/>. Policy
    /// ladder (all inside a single transaction):
    /// <list type="number">
    /// <item>Tagged row at <paramref name="claimEmail"/> → <see cref="ReconcileOutcome.NoChange"/>.</item>
    /// <item>Tagged row at a different email + another row of this user holds
    /// the claim email → tag-move (union <c>IsGoogle</c>/<c>IsPrimary</c>,
    /// force verify, delete old) → <see cref="ReconcileOutcome.TagMoved"/>.</item>
    /// <item>Tagged row at a different email + no other row of this user holds
    /// the claim email → rewrite → <see cref="ReconcileOutcome.EmailRewritten"/>.</item>
    /// <item>No tagged row + another row of this user holds the claim email →
    /// tag-move → <see cref="ReconcileOutcome.TagMoved"/>.</item>
    /// <item>No tagged row + no row of this user holds the claim email → insert
    /// a verified tagged row → <see cref="ReconcileOutcome.NewRowCreated"/>.</item>
    /// </list>
    /// Before any insert/rewrite that would place a verified row at
    /// <paramref name="claimEmail"/>, a cross-user collision check runs. If
    /// another user already verified-holds the claim email:
    /// <list type="bullet">
    /// <item>When <paramref name="claimEmailVerified"/> is <c>true</c> → Google
    /// wins. Delete the other user's row, write paired audits, proceed →
    /// <see cref="ReconcileOutcome.CrossUserDisplaced"/>.</item>
    /// <item>When <paramref name="claimEmailVerified"/> is <c>false</c> →
    /// block. No mutation. Audit the attempt →
    /// <see cref="ReconcileOutcome.CrossUserBlocked"/>.</item>
    /// </list>
    /// Audit rows for every outcome are written by this method inside the same
    /// transaction as the data change; callers must not write their own audit
    /// rows on the OAuth path. Sign-in must NEVER block on this call —
    /// exceptions propagate but the caller is required to swallow them and
    /// continue the sign-in.
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
/// Thrown by <see cref="IUserEmailService.ReconcileOAuthIdentityAsync"/>
/// (via the underlying repository) when the verified-email partial unique
/// index catches a concurrent cross-user collision past the in-service
/// pre-check — the race backstop. Surfaced as a domain exception so the
/// Web layer never imports Postgres / Npgsql types
/// (<c>docs/architecture/design-rules.md §1</c>: clean architecture).
/// </summary>
public sealed class OAuthReconcileConcurrencyException : Exception
{
    public OAuthReconcileConcurrencyException() { }
    public OAuthReconcileConcurrencyException(string message) : base(message) { }
    public OAuthReconcileConcurrencyException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Outcome of <see cref="IUserEmailService.ReconcileOAuthIdentityAsync"/>.
/// </summary>
public enum ReconcileOutcome
{
    /// <summary>User's tagged row already holds the claim email.</summary>
    NoChange,
    /// <summary>Tagged row's email was rewritten in place.</summary>
    EmailRewritten,
    /// <summary>Provider tag moved onto another row of the same user holding
    /// the claim email; flags unioned, old row deleted.</summary>
    TagMoved,
    /// <summary>No tagged row existed; created a new verified tagged row.</summary>
    NewRowCreated,
    /// <summary>Another user already verified-held the claim email; that
    /// user's row was deleted (Google wins) because the provider's claim
    /// asserted email verification.</summary>
    CrossUserDisplaced,
    /// <summary>Another user already verified-held the claim email and the
    /// provider's claim did not assert email verification. No mutation
    /// performed.</summary>
    CrossUserBlocked,
}

/// <summary>
/// Informational record describing the reconcile outcome. Audit rows are
/// written by the service itself; this record exists for tests, admin
/// diagnostics, and any non-audit observers in the caller.
/// </summary>
/// <param name="Outcome">The reconcile outcome that occurred.</param>
/// <param name="PreviousEmail">For <see cref="ReconcileOutcome.EmailRewritten"/>
/// or <see cref="ReconcileOutcome.TagMoved"/>, the email value previously
/// held by the signing user's tagged row. <c>null</c> otherwise.</param>
/// <param name="AffectedRowId">The <see cref="UserEmail.Id"/> of the signing
/// user's row that now carries the tag at the claim email, or that was
/// determined to already be in the correct state. <c>null</c> for
/// <see cref="ReconcileOutcome.CrossUserBlocked"/>.</param>
/// <param name="DisplacedUserId">For <see cref="ReconcileOutcome.CrossUserDisplaced"/>
/// or <see cref="ReconcileOutcome.CrossUserBlocked"/>, the id of the other
/// user that held / holds the claim email.</param>
/// <param name="DisplacedRowId">For <see cref="ReconcileOutcome.CrossUserDisplaced"/>,
/// the id of the row that was deleted. For
/// <see cref="ReconcileOutcome.CrossUserBlocked"/>, the id of the row that
/// blocked the mutation.</param>
/// <param name="DisplacedEmail">The email value on the displaced (or blocking)
/// row.</param>
/// <param name="DisplacedUserLeftWithoutVerifiedEmail"><c>true</c> when
/// <see cref="ReconcileOutcome.CrossUserDisplaced"/> deleted the displaced
/// user's only remaining verified email row.</param>
public record OAuthReconcileResult(
    ReconcileOutcome Outcome,
    string? PreviousEmail,
    Guid? AffectedRowId,
    Guid? DisplacedUserId,
    Guid? DisplacedRowId,
    string? DisplacedEmail,
    bool DisplacedUserLeftWithoutVerifiedEmail);

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

/// <summary>
/// Per-user summary of UserEmail flag-invariant violations. Returned by
/// <see cref="IUserEmailService.GetEmailFlagViolationsAsync"/> for the
/// admin remediation screen.
/// </summary>
/// <param name="UserId">The user with the violation.</param>
/// <param name="DisplayName">Display name (for the admin grid). May be null
/// if the User row is missing or has no display name.</param>
/// <param name="IsGoogleCount">How many rows have <c>IsGoogle = true</c>.
/// A healthy value is 1 (when verified rows exist); 0 with verified rows is
/// a violation, as are values &gt; 1.</param>
/// <param name="VerifiedCount">How many rows are verified.</param>
/// <param name="VerifiedPrimaryCount">How many verified rows have
/// <c>IsPrimary = true</c>. A healthy value is 1 (when verified rows exist)
/// or 0 (when no verified rows exist).</param>
/// <param name="HasMultipleGoogle">Convenience flag — true when
/// <see cref="IsGoogleCount"/> &gt; 1.</param>
/// <param name="HasZeroGoogle">Convenience flag — true when verified rows
/// exist and <see cref="IsGoogleCount"/> is 0.</param>
/// <param name="HasPrimaryProblem">Convenience flag — true when verified
/// rows exist and <see cref="VerifiedPrimaryCount"/> is not exactly 1.</param>
public record UserEmailFlagViolation(
    Guid UserId,
    string? DisplayName,
    int IsGoogleCount,
    int VerifiedCount,
    int VerifiedPrimaryCount,
    bool HasMultipleGoogle,
    bool HasZeroGoogle,
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
    bool IsGoogle,
    bool IsPrimary,
    ContactFieldVisibility? Visibility,
    Instant? VerificationSentAt,
    Instant CreatedAt,
    Instant UpdatedAt);
