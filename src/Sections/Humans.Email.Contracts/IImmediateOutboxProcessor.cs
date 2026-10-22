namespace Humans.Email.Contracts;

/// <summary>
/// Queues an immediate outbox run for time-sensitive mail. Best effort: if it fails, the one-minute recurring
/// run still delivers.
/// </summary>
public interface IImmediateOutboxProcessor
{
    void TriggerImmediate();
}
