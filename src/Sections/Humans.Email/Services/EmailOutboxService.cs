using Humans.Email.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Data;
using Humans.Base.Attributes;
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

/// <summary>
/// The implementation of <see cref="IEmailOutboxService"/>:
/// admin-dashboard reads (stats, recent messages, per-user history) and admin
/// writes (retry, discard, pause/resume) over <see cref="IEmailOutboxRepository"/>.
/// Authoritative gateway for the <c>IsEmailSendingPaused</c> flag; the background
/// processor job reads it through <see cref="IsEmailPausedAsync"/>. Also owns the
/// retention cutoff <c>CleanupEmailOutboxJob</c> drives through
/// <see cref="IEmailOutboxRetention"/>. Also the section's GDPR fan-out member:
/// outbox rows are user-scoped personal data (address, name, rendered body).
/// </summary>
[CrossSectionWrite("Email owns the IsEmailSendingPaused flag; the Settings key/value store is where it is kept.")]
internal sealed class EmailOutboxService(
    IEmailOutboxRepository repo,
    ISettingsService settingsStore,
    IOptions<EmailSettings> settings,
    IClock clock,
    IAuditLogService audit) : IEmailOutboxService, IUserDataContributor, IUserMerge
{
    internal const string EmailOutbox = "EmailOutbox";

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

    // --- Daily send counts (#1195) ---

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

    public async Task<int> BackfillDailySendCountsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ComputeMissingBackfillRowsAsync(cancellationToken);
        if (rows.Count == 0) return 0;

        await repo.AddDailySendCountsAsync(rows, cancellationToken);
        return rows.Count;
    }

    /// <summary>
    /// Aggregates retained <c>Sent</c> outbox rows into (Date, TemplateName) daily
    /// <c>SentCount</c>s. Per the issue #1195 spec, failed sends are never
    /// reconstructed here — the outbox keeps only a message's final state, not a
    /// per-attempt history, so a failed message's actual failure day (and how many
    /// attempts it took) cannot be recovered; <c>FailedCount</c> stays 0 for every
    /// backfilled row. Drops today's UTC date entirely (not just existing keys for
    /// it) and any (Date, TemplateName) combination that already has a row, so a
    /// day the live processor has started — or finished — counting is never
    /// double-touched or left permanently short by a partial-day merge.
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

    // --- IUserDataContributor ---

    /// <summary>
    /// GDPR Article 15 slice — the same per-user outbox history the human already
    /// reads at <c>/Profile/Me/Outbox</c>, uncapped.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var messages = await repo.GetForUserAsync(userId, ct);

        var shaped = messages.Select(m => new
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

        return [new UserDataSlice(EmailOutbox, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [EmailOutbox] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// GDPR Article 17: drops every outbox row attributed to the human, whatever
    /// its status. The retention sweep only reaches <c>Sent</c> rows past the
    /// cutoff, so failed and queued rows would otherwise outlive the erasure.
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
