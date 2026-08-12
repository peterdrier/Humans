namespace Humans.Email.Contracts;

/// <summary>
/// The outbox drain <c>ProcessEmailOutboxJob</c> runs. One method, so the job stays a
/// scheduler shim and the queue semantics — the pause check, the pick-up window, the
/// per-message transport call, the retry backoff and the campaign-grant status mirror —
/// live inside the section beside the repository they read through (design §15 step 6b;
/// the same carve as Notifications' <c>INotificationRetention</c> and Agent's
/// <c>IAgentConversationRetention</c>). Before it, the job injected
/// <c>IEmailOutboxRepository</c> and <c>IEmailTransport</c> directly, which is a job
/// skipping the service layer into another assembly's table.
/// </summary>
public interface IEmailOutboxProcessor
{
    /// <summary>
    /// Drains one batch of queued messages. Returns immediately when sending is paused.
    /// Never throws for a single message's delivery failure — that row is marked failed
    /// and retried on a later tick.
    /// </summary>
    Task ProcessQueuedAsync(CancellationToken cancellationToken = default);
}
