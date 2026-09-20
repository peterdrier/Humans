using NodaTime;

namespace Humans.Email.Domain;

/// <summary>
/// One UTC-day/template send tally — durable denominator for spam-rate spikes
/// (nobodies-collective/Humans#1195). Never purged by <c>CleanupEmailOutboxJob</c>,
/// unlike <see cref="EmailOutboxMessage"/>, which is pruned after
/// <c>Email:OutboxRetentionDays</c>.
/// </summary>
internal sealed class EmailDailySendCount
{
    public LocalDate Date { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
}
