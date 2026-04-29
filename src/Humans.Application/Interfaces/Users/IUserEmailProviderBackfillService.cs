namespace Humans.Application.Interfaces.Users;

/// <summary>
/// One-shot admin-button backfill that populates the new
/// <c>UserEmail.Provider</c> / <c>UserEmail.ProviderKey</c> / <c>UserEmail.IsGoogle</c>
/// columns added in PR 3 of the email-identity-decoupling spec
/// (<c>docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md</c>).
///
/// <para>
/// Runs operator-triggered: clicked once on QA, verified, then once on
/// production, verified, before PR 7 ships the legacy column drops. Idempotent
/// — safe to re-run; rows already populated are skipped. Audit-logged per
/// <see cref="Domain.Entities.UserEmail"/> row updated.
/// </para>
/// </summary>
public interface IUserEmailProviderBackfillService
{
    Task<UserEmailProviderBackfillResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a single <see cref="IUserEmailProviderBackfillService.RunAsync"/>
/// invocation. Surfaces operator-visible counters plus a list of warnings for
/// any user whose <c>AspNetUserLogins</c> rows could not be uniquely matched
/// to a <see cref="Domain.Entities.UserEmail"/> row.
/// </summary>
public sealed record UserEmailProviderBackfillResult(
    int UsersProcessed,
    int ProviderRowsUpdated,
    int IsGoogleRowsUpdated,
    int AmbiguousMatchesWarned,
    IReadOnlyList<string> Warnings);
