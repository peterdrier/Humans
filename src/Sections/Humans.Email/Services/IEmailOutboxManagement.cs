using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Email.Services;

internal interface IEmailOutboxManagement : IEmailOutboxService, IEmailOutboxRetention, IApplicationService
{
    Task<EmailOutboxStats> GetOutboxStatsAsync(
        int recentMessageCount = 50, CancellationToken cancellationToken = default);

    /// <summary>Returns the recipient address, or null when the message doesn't exist.</summary>
    Task<string?> RetryMessageAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Returns the recipient address, or null when the message doesn't exist.</summary>
    Task<string?> DiscardMessageAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default);

    Task SetEmailPausedAsync(bool paused, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Includes the top templates by volume within the window.</summary>
    Task<DailySendCountsDto> GetDailySendCountsAsync(int days = 90, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: the (Date, TemplateName) rows that retained outbox rows would add, shown to the admin
    /// before they confirm <see cref="BackfillDailySendCountsAsync"/>.
    /// </summary>
    Task<BackfillPreviewDto> PreviewDailySendCountBackfillAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds only keys with no row yet, so processor counts are never overwritten and a re-run is safe.
    /// </summary>
    Task<int> BackfillDailySendCountsAsync(Guid actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>Totals for one UTC day, summed across templates.</summary>
internal sealed record DailySendCountRow(LocalDate Date, int SentCount, int FailedCount);

internal sealed record TemplateSendCountRow(string TemplateName, int SentCount, int FailedCount);

internal sealed record DailySendCountsDto(
    IReadOnlyList<DailySendCountRow> ByDay,
    IReadOnlyList<TemplateSendCountRow> TopTemplates);

internal sealed record DailyTemplateSendCountRow(LocalDate Date, string TemplateName, int SentCount, int FailedCount);

internal sealed record BackfillPreviewDto(
    int RowsToAdd,
    LocalDate? EarliestDate,
    LocalDate? LatestDate,
    IReadOnlyList<DailyTemplateSendCountRow> Sample);

internal sealed record EmailOutboxStats(
    int TotalCount,
    int QueuedCount,
    int SentLast24HoursCount,
    int FailedCount,
    bool IsPaused,
    IReadOnlyList<EmailOutboxMessageDto> RecentMessages);
