using Humans.AuditLog.Contracts;
using Humans.Email.Data;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Microsoft.Extensions.Options;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Email.Domain;
using Humans.Base.Enums;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Email.Services;

internal sealed class EmailOutboxService(
    IEmailOutboxRepository repo,
    ISettingsService settingsStore,
    IOptions<EmailSettings> settings,
    IClock clock,
    IAuditLogService audit) : IEmailOutboxManagement, IUserDataContributor, IUserMerge
{
    internal const string EmailOutbox = "EmailOutbox";
    internal const string EmailOutboxMergedAccounts = "EmailOutboxMergedAccounts";

    private static readonly Duration Last24Hours = Duration.FromHours(24);

    private readonly EmailSettings _settings = settings.Value;

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetCurrentInstant() - Duration.FromDays(_settings.OutboxRetentionDays);
        return repo.DeleteSentOlderThanAsync(cutoff, cancellationToken);
    }

    public async Task<string?> RetryMessageAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var recipient = await repo.RetryAsync(id, cancellationToken);
        if (recipient is not null)
            await audit.LogAsync(AuditAction.EmailOutboxRetried, nameof(EmailOutboxMessage), id,
                "Queued outbox message for retry.", actorUserId);
        return recipient;
    }

    public async Task<string?> DiscardMessageAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var recipient = await repo.DiscardAsync(id, cancellationToken);
        if (recipient is not null)
            await audit.LogAsync(AuditAction.EmailOutboxDiscarded, nameof(EmailOutboxMessage), id,
                "Discarded outbox message.", actorUserId);
        return recipient;
    }

    public async Task<EmailOutboxStats> GetOutboxStatsAsync(
        int recentMessageCount = 50, CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var cutoff24H = now - Last24Hours;

        var totalCount = await repo.GetTotalCountAsync(cancellationToken);
        var queuedCount = await repo.GetCountByStatusAsync(EmailOutboxStatus.Queued, cancellationToken);
        var sentLast24H = await repo.GetSentCountSinceAsync(cutoff24H, cancellationToken);
        var failedCount = await repo.GetCountByStatusAsync(EmailOutboxStatus.Failed, cancellationToken);
        var isPaused = await IsEmailPausedAsync(cancellationToken);
        var messages = await repo.GetRecentAsync(recentMessageCount, cancellationToken);

        return new EmailOutboxStats(
            totalCount,
            queuedCount,
            sentLast24H,
            failedCount,
            isPaused,
            messages.Select(ToDto).ToList());
    }

    public async Task<IReadOnlyList<EmailOutboxMessageDto>> GetMessagesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var messages = await repo.GetForUserAsync(userId, cancellationToken);
        return messages.Select(ToDto).ToList();
    }

    public Task<int> GetMessageCountForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        repo.GetCountForUserAsync(userId, cancellationToken);

    public async Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default)
    {
        var value = await settingsStore.GetValueAsync(
            SettingKeys.IsEmailSendingPaused,
            cancellationToken);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetEmailPausedAsync(bool paused, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        await settingsStore.SetValueAsync(SettingKeys.IsEmailSendingPaused,
            paused ? "true" : "false", cancellationToken);
        await audit.LogAsync(paused ? AuditAction.EmailSendingPaused : AuditAction.EmailSendingResumed,
            "EmailSending", Guid.Empty, paused ? "Paused email sending." : "Resumed email sending.", actorUserId);
    }

    public async Task<DailySendCountsDto> GetDailySendCountsAsync(
        int days = 90, CancellationToken cancellationToken = default)
    {
        var since = clock.GetCurrentInstant().InUtc().Date.PlusDays(-(days - 1));
        var rows = await repo.GetDailySendCountsSinceAsync(since, cancellationToken);

        var byDay = rows
            .GroupBy(r => r.Date)
            .Select(g => new DailySendCountRow(g.Key, g.Sum(r => r.SentCount), g.Sum(r => r.FailedCount)))
            .OrderByDescending(r => r.Date)
            .ToList();

        var topTemplates = rows
            .GroupBy(r => r.TemplateName, StringComparer.Ordinal)
            .Select(g => new TemplateSendCountRow(g.Key, g.Sum(r => r.SentCount), g.Sum(r => r.FailedCount)))
            .OrderByDescending(r => r.SentCount + r.FailedCount)
            .Take(10)
            .ToList();

        return new DailySendCountsDto(byDay, topTemplates);
    }

    public async Task<BackfillPreviewDto> PreviewDailySendCountBackfillAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await ComputeMissingBackfillRowsAsync(cancellationToken);
        return new BackfillPreviewDto(
            rows.Count,
            rows.Count == 0 ? null : rows.Min(r => r.Date),
            rows.Count == 0 ? null : rows.Max(r => r.Date),
            rows.Take(50)
                .Select(r => new DailyTemplateSendCountRow(r.Date, r.TemplateName, r.SentCount, r.FailedCount))
                .ToList());
    }

    public async Task<int> BackfillDailySendCountsAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var rows = await ComputeMissingBackfillRowsAsync(cancellationToken);
        if (rows.Count > 0)
            await repo.AddDailySendCountsAsync(rows, cancellationToken);

        await audit.LogAsync(AuditAction.EmailDailySendCountsBackfilled, nameof(EmailDailySendCount), Guid.Empty,
            $"Backfilled {rows.Count} daily send count row(s) from outbox history", actorUserId);
        return rows.Count;
    }

    /// <summary>
    /// Rebuilds sent counts from retained Sent rows. Failures can't be rebuilt: the outbox keeps only each
    /// message's final state, so FailedCount stays 0. Today and any key that already has a row are skipped,
    /// so a day the live processor has counted is never touched or left short.
    /// </summary>
    private async Task<IReadOnlyList<EmailDailySendCount>> ComputeMissingBackfillRowsAsync(
        CancellationToken cancellationToken)
    {
        var since = clock.GetCurrentInstant() - Duration.FromDays(_settings.OutboxRetentionDays);
        var today = clock.GetCurrentInstant().InUtc().Date;
        var messages = await repo.GetSentSinceAsync(since, cancellationToken);
        var existingKeys = await repo.GetDailySendCountKeysAsync(cancellationToken);

        return messages
            .Where(m => !EmailTestAddress.IsTestAddress(m.RecipientEmail))
            .GroupBy(m => (Date: m.SentAt!.Value.InUtc().Date, m.TemplateName))
            .Where(g => g.Key.Date != today && !existingKeys.Contains((g.Key.Date, g.Key.TemplateName)))
            .Select(g => new EmailDailySendCount
            {
                Date = g.Key.Date,
                TemplateName = g.Key.TemplateName,
                SentCount = g.Count(),
                FailedCount = 0
            })
            .OrderBy(r => r.Date).ThenBy(r => r.TemplateName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Uncapped: the same history the member sees at <c>/Email/Outbox</c>.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var messages = await repo.GetForUserAsync(userId, ct);
        return [new UserDataSlice(EmailOutbox, Shape(messages))];
    }

    /// <summary>
    /// Rows still keyed to archived accounts. Older merges left them behind, and Email may not call Users to
    /// find them itself.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForIdentityChainAsync(
        Guid userId, IReadOnlyList<Guid> allUserIds, CancellationToken ct)
    {
        var archivedIds = allUserIds.Where(id => id != userId).ToList();
        if (archivedIds.Count == 0)
            return [];

        var messages = new List<EmailOutboxMessage>();
        foreach (var id in archivedIds)
            messages.AddRange(await repo.GetForUserAsync(id, ct));

        return [new UserDataSlice(EmailOutboxMergedAccounts,
            Shape(messages.OrderByDescending(m => m.CreatedAt).ToList()))];
    }

    private static List<object> Shape(IEnumerable<EmailOutboxMessage> messages) =>
        messages.Select(object (m) => new
        {
            m.RecipientEmail,
            m.RecipientName,
            m.Subject,
            m.HtmlBody,
            m.TemplateName,
            m.Status,
            CreatedAt = m.CreatedAt.ToIso8601(),
            SentAt = m.SentAt.ToIso8601()
        }).ToList();

    // Erasure runs per id across the whole chain, so archived ids' rows go too.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [EmailOutbox] = null,
            [EmailOutboxMergedAccounts] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// Every status goes: the retention sweep only reaches old Sent rows, so failed and queued rows would
    /// outlive the erasure.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repo.DeleteForUserAsync(userId, ct);

    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct) =>
        repo.ReassignToUserAsync(mergedFromUserId, mergedToUserId, ct);

    private static EmailOutboxMessageDto ToDto(EmailOutboxMessage message) => new(
        message.Id,
        message.RecipientEmail,
        message.RecipientName,
        message.Subject,
        message.HtmlBody,
        message.TemplateName,
        message.UserId,
        message.Status,
        message.CreatedAt,
        message.SentAt,
        message.RetryCount,
        message.LastError);
}
