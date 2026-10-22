namespace Humans.Email.Services;

/// <summary>
/// Queues an immediate outbox run for urgent mail. Best effort: if it fails, the one-minute recurring
/// run still delivers.
/// </summary>
internal interface IImmediateOutboxProcessor
{
    void TriggerImmediate();
}
