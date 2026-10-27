using Humans.Base.Attributes;
namespace Humans.Email.Services;

internal interface IEmailOutboxProcessor
{
    /// <summary>
    /// Drains one batch; returns at once while sending is paused. A single message's failure never throws: the row
    /// is marked failed and retried later.
    /// </summary>
    [ExternalWrite]
    Task ProcessQueuedAsync(CancellationToken cancellationToken = default);
}
