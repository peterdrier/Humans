using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>
/// Scans every UserEmail invariant violation surface for the
/// <c>/Profile/Admin/EmailProblems</c> page. Consumes only existing section
/// services — never any <c>I*Repository</c> or <c>DbContext</c>.
/// </summary>
public interface IEmailProblemsService : IApplicationService
{
    Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Backfills a verified <c>UserEmail</c> row from
    /// <c>User.IdentityEmailColumn</c> (the raw legacy AspNetIdentity column)
    /// for every user currently flagged by case 9
    /// (<see cref="EmailProblemKind.LegacyIdentityEmailNotInUserEmails"/>).
    /// Idempotent — relies on
    /// <c>IUserEmailService.AddVerifiedEmailAsync</c>'s existing skip-if-exists
    /// check. Audits <c>LegacyIdentityEmailBackfilled</c> for each created row
    /// against <paramref name="actorUserId"/>, and returns the (userId, email)
    /// pairs that received one. Run from the <c>users.legacy-identity-emails</c>
    /// data migration on <c>/Debug/DataMigrations</c>.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, string Email)>> BackfillLegacyIdentityEmailsAsync(
        Guid actorUserId, CancellationToken ct = default);
}
