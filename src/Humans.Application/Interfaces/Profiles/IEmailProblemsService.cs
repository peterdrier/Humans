using Humans.Application.Interfaces;
using Humans.Application.DTOs.EmailProblems;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Scans every UserEmail invariant violation surface for the
/// <c>/Profile/Admin/EmailProblems</c> page. Consumes only existing section
/// services — never any <c>I*Repository</c> or <c>DbContext</c>.
/// </summary>
public interface IEmailProblemsService : IApplicationService
{
    Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if the two users currently share any UserEmail address
    /// under the same normalization rule used by <see cref="ScanAsync"/>'s
    /// case-5 detection (raw match plus gmail/googlemail equivalence).
    /// Used by the admin merge POST to re-verify that the submitted pair
    /// is actually an email-conflict pair before tombstoning, since form
    /// fields are client-controlled.
    /// </summary>
    Task<bool> UsersShareAnyEmailAsync(Guid user1Id, Guid user2Id, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user is currently in the "ghost external logins"
    /// set (has <c>AspNetUserLogins</c> rows but zero <c>UserEmail</c> rows).
    /// Used by the admin "delete ghost logins" POST to re-verify the user
    /// is still a ghost before deleting auth-table rows, since form fields
    /// are client-controlled and the report may be stale.
    /// </summary>
    Task<bool> IsGhostExternalLoginsUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Backfills a verified <c>UserEmail</c> row from
    /// <c>User.IdentityEmailColumn</c> (the raw legacy AspNetIdentity column)
    /// for every user currently flagged by case 9
    /// (<see cref="EmailProblemKind.LegacyIdentityEmailNotInUserEmails"/>).
    /// Idempotent — relies on
    /// <c>IUserEmailService.AddVerifiedEmailAsync</c>'s existing skip-if-exists
    /// check. Returns the (userId, email) pairs that received a new row, so
    /// the caller can audit per row.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, string Email)>> BackfillLegacyIdentityEmailsAsync(
        CancellationToken ct = default);
}
