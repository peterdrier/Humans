using NodaTime;

namespace Humans.Email.Domain;

/// <summary>
/// One UTC day's sends for one template: the lasting denominator for spam-rate spikes. Kept after the outbox rows
/// are pruned.
/// </summary>
internal sealed class EmailDailySendCount
{
    public LocalDate Date { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
}
