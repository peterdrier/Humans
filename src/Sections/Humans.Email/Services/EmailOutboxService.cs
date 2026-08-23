using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Base.Attributes;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Microsoft.Extensions.Options;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Email.Domain;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Email.Services;

/// <summary>
/// Application-layer implementation of <see cref="IEmailOutboxService"/>:
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
    IClock clock) : IEmailOutboxService, IUserDataContributor
{
    private static readonly Duration Last24Hours = Duration.FromHours(24);

    private readonly EmailSettings _settings = settings.Value;

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetCurrentInstant() - Duration.FromDays(_settings.OutboxRetentionDays);
        return repo.DeleteSentOlderThanAsync(cutoff, cancellationToken);
    }

    public Task<string?> RetryMessageAsync(Guid id, CancellationToken cancellationToken = default) =>
        repo.RetryAsync(id, cancellationToken);

    public Task<string?> DiscardMessageAsync(Guid id, CancellationToken cancellationToken = default) =>
        repo.DiscardAsync(id, cancellationToken);

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

    public Task SetEmailPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        settingsStore.SetValueAsync(
            SettingKeys.IsEmailSendingPaused,
            paused ? "true" : "false",
            cancellationToken);

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

        return [new UserDataSlice(GdprExportSections.EmailOutbox, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.EmailOutbox] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// GDPR Article 17: drops every outbox row attributed to the human, whatever
    /// its status. The retention sweep only reaches <c>Sent</c> rows past the
    /// cutoff, so failed and queued rows would otherwise outlive the erasure.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repo.DeleteForUserAsync(userId, ct);

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
