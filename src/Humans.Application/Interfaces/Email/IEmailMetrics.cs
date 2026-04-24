namespace Humans.Application.Interfaces.Email;

/// <summary>
/// Email section's metrics surface (issue nobodies-collective/Humans#580).
/// Call sites inject this interface directly; counter names live with the
/// implementation rather than the cross-cutting <c>IHumansMetrics</c>.
/// </summary>
public interface IEmailMetrics
{
    /// <summary>
    /// Increments <c>humans.emails_sent_total</c> with <c>template</c> tag.
    /// Called by SMTP and outbox-processing paths after a successful send.
    /// </summary>
    void RecordSent(string template);

    /// <summary>
    /// Increments <c>humans.email_queued_total</c> with <c>template</c> tag.
    /// Called by the outbox-queue writer when a new message is enqueued.
    /// </summary>
    void RecordQueued(string template);

    /// <summary>
    /// Increments <c>humans.email_failed_total</c> with <c>template</c> tag.
    /// Called by the outbox processor when a send fails permanently.
    /// </summary>
    void RecordFailed(string template);

    /// <summary>
    /// Pushes the current outbox-pending count into the
    /// <c>humans.email_outbox_pending</c> gauge. Written by the outbox
    /// processor after each sweep.
    /// </summary>
    void SetOutboxPending(int count);
}
