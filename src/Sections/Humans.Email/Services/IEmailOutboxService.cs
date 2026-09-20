using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using NodaTime;

namespace Humans.Email.Services;

/// <summary>
/// The outbox admin surface: retry, discard, pause/resume. Internal —
/// its only consumer is the section's own <c>EmailController</c>. The per-user history
/// reads and the dashboard stats it inherits from <see cref="IEmailOutboxServiceRead"/>
/// are the half that leaves, on the contracts leaf, for Shell's profile, user-admin
/// outbox pages, and the admin dashboard.
/// </summary>
/// <remarks>
/// The interface survives the internalise pass because <c>MA0053</c> seals the concrete
/// service and Castle DynamicProxy cannot substitute a sealed class (the G5 playbook, step 5,
/// Budget's rule) — <c>EmailOutboxServiceTests</c> and the controller tests stub it.
/// </remarks>
internal interface IEmailOutboxService : IEmailOutboxServiceRead, IEmailOutboxRetention, IApplicationService
{
    /// <summary>
    /// Requeues a failed or stuck email outbox message for retry.
    /// Returns the recipient email if found, or null if the message does not exist.
    /// </summary>
    Task<string?> RetryMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards (deletes) an email outbox message.
    /// Returns the recipient email if found, or null if the message does not exist.
    /// </summary>
    Task<string?> DiscardMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether email sending is currently paused.
    /// </summary>
    Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the email sending paused state.
    /// </summary>
    Task SetEmailPausedAsync(bool paused, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate daily send/failure counts for the outbox dashboard's rolling
    /// window, plus the top templates by volume within it.
    /// </summary>
    Task<DailySendCountsDto> GetDailySendCountsAsync(int days = 90, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes what a backfill would add — every (Date, TemplateName) combination
    /// derived from retained outbox rows that has no daily-count row yet — without
    /// writing anything. Powers the admin review step before <see cref="BackfillDailySendCountsAsync"/>.
    /// </summary>
    Task<BackfillPreviewDto> PreviewDailySendCountBackfillAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the rows <see cref="PreviewDailySendCountBackfillAsync"/> would show, for
    /// (Date, TemplateName) combinations that still have no existing row — this never
    /// overwrites processor-written (or previously backfilled) data, so it's safe to
    /// re-run. Returns the number of rows added.
    /// </summary>
    Task<int> BackfillDailySendCountsAsync(CancellationToken cancellationToken = default);
}

/// <summary>One UTC day's send/failure totals, summed across templates.</summary>
internal sealed record DailySendCountRow(LocalDate Date, int SentCount, int FailedCount);

/// <summary>One template's send/failure totals, summed across the dashboard's window.</summary>
internal sealed record TemplateSendCountRow(string TemplateName, int SentCount, int FailedCount);

/// <summary>The outbox dashboard's daily-volume section.</summary>
internal sealed record DailySendCountsDto(
    IReadOnlyList<DailySendCountRow> ByDay,
    IReadOnlyList<TemplateSendCountRow> TopTemplates);

/// <summary>One (Date, TemplateName) row a backfill would add.</summary>
internal sealed record DailyTemplateSendCountRow(LocalDate Date, string TemplateName, int SentCount, int FailedCount);

/// <summary>
/// The review step of the backfill's review-then-confirm flow
/// (memory/process/no-data-backfills.md) — what would be added, with a sample to
/// sanity-check before the operator clicks confirm.
/// </summary>
internal sealed record BackfillPreviewDto(
    int RowsToAdd,
    LocalDate? EarliestDate,
    LocalDate? LatestDate,
    IReadOnlyList<DailyTemplateSendCountRow> Sample);
