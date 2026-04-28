namespace Humans.Application.Interfaces.Users;

/// <summary>
/// One-shot administrative operation that creates a
/// <see cref="Domain.Entities.UserEmail"/> row for every
/// <see cref="Domain.Entities.User"/> that has none. Idempotent: a successful
/// run leaves no orphans and re-running afterwards reports
/// <see cref="UserEmailBackfillResult.RowsInserted"/> = 0.
///
/// <para>
/// Introduced in PR 1 of the email-identity-decoupling spec
/// (<c>docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md</c>).
/// The PR 2 migration also runs an idempotent defensive backfill before
/// dropping the Identity email columns, so this button is the preferred
/// pre-deploy operator path (audit-logged, surfaces skipped users with no
/// <c>User.Email</c>) but the migration itself is the last line of defence.
/// </para>
/// </summary>
public interface IUserEmailBackfillService
{
    /// <summary>
    /// Backfills missing <see cref="Domain.Entities.UserEmail"/> rows from the
    /// orphan User's <c>Email</c> / <c>EmailConfirmed</c> columns. Users with
    /// no <c>Email</c> populated are reported via
    /// <see cref="UserEmailBackfillResult.SkippedUserIds"/> for admin review —
    /// they can't be auto-fixed because there is no email to seed from.
    /// </summary>
    Task<UserEmailBackfillResult> BackfillAsync(CancellationToken ct = default);
}

/// <summary>
/// Outcome of a <see cref="IUserEmailBackfillService.BackfillAsync"/> run.
/// </summary>
/// <param name="OrphansFound">
/// Total Users that had no UserEmail row at the start of the run.
/// </param>
/// <param name="RowsInserted">
/// UserEmail rows actually inserted. Equals
/// <paramref name="OrphansFound"/> minus <paramref name="SkippedUserIds"/>.Count
/// when there are no errors mid-run.
/// </param>
/// <param name="SkippedUserIds">
/// Orphan User ids that could not be auto-backfilled because they had no
/// <c>User.Email</c> to copy from. The admin must triage these manually.
/// </param>
public sealed record UserEmailBackfillResult(
    int OrphansFound,
    int RowsInserted,
    IReadOnlyList<Guid> SkippedUserIds);
