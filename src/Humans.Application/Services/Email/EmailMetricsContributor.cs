using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;

namespace Humans.Application.Services.Email;

/// <summary>
/// Email section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Owns the <c>humans.emails_sent_total</c>, <c>humans.email_queued_total</c>,
/// <c>humans.email_failed_total</c> counters and the
/// <c>humans.email_outbox_pending</c> gauge. Registered as a Singleton so the
/// <see cref="Counter{T}"/> references and pending-count state persist across scopes.
/// </summary>
public sealed class EmailMetricsContributor : IEmailMetrics, IMetricsContributor
{
    private Counter<long> _sent = null!;
    private Counter<long> _queued = null!;
    private Counter<long> _failed = null!;
    private volatile int _outboxPending;

    public void Initialize(IHumansMetrics metrics)
    {
        _sent = metrics.CreateCounter<long>(
            "humans.emails_sent_total",
            description: "Total emails sent");
        _queued = metrics.CreateCounter<long>(
            "humans.email_queued_total",
            description: "Total emails queued for sending");
        _failed = metrics.CreateCounter<long>(
            "humans.email_failed_total",
            description: "Total email send failures");

        metrics.CreateObservableGauge<int>(
            "humans.email_outbox_pending",
            observeValue: () => _outboxPending,
            description: "Emails pending in the outbox queue");
    }

    public void RecordSent(string template) =>
        _sent.Add(1, new KeyValuePair<string, object?>("template", template));

    public void RecordQueued(string template) =>
        _queued.Add(1, new KeyValuePair<string, object?>("template", template));

    public void RecordFailed(string template) =>
        _failed.Add(1, new KeyValuePair<string, object?>("template", template));

    public void SetOutboxPending(int count) => _outboxPending = count;
}
