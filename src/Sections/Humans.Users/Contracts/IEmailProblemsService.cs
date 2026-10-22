using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>Reads only through the section's services, never a repository or <c>DbContext</c>.</summary>
public interface IEmailProblemsService : IApplicationService
{
    Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a verified <c>UserEmail</c> row from the legacy Identity column for every user flagged
    /// <see cref="EmailProblemKind.LegacyIdentityEmailNotInUserEmails"/>, audited to <paramref name="actorUserId"/>.
    /// Idempotent. Returns the pairs that got a row.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, string Email)>> BackfillLegacyIdentityEmailsAsync(
        Guid actorUserId, CancellationToken ct = default);
}
