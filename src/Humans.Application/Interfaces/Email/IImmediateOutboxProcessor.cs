namespace Humans.Application.Interfaces.Email;

/// <summary>
/// Triggers an immediate run of the background email-outbox processor for
/// time-sensitive templates (email verification, magic link, workspace
/// credentials). Abstracts the Hangfire <c>IBackgroundJobClient</c> so
/// <c>OutboxEmailService</c> can live in <see cref="Application"/>
/// without taking a dependency on the Hangfire runtime.
/// </summary>
/// <remarks>
/// The implementation lives in <c>Humans.Infrastructure</c> and enqueues a
/// one-off run of <c>ProcessEmailOutboxJob</c> in addition to the recurring
/// 1-minute schedule. This is best-effort: if the scheduler is
/// unreachable, the recurring run still delivers the message within a
/// minute.
/// </remarks>
public interface IImmediateOutboxProcessor
{
    /// <summary>
    /// Enqueues an immediate outbox-processor run. Non-blocking: the run is
    /// dispatched through the background-job infrastructure.
    /// </summary>
    void TriggerImmediate();
}
