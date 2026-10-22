using Hangfire;
using Humans.Email.Jobs;

namespace Humans.Email.Contracts;

public sealed class HangfireImmediateOutboxProcessor(IBackgroundJobClient backgroundJobClient)
    : IImmediateOutboxProcessor
{
    public void TriggerImmediate() =>
        backgroundJobClient.Enqueue<ProcessEmailOutboxJob>(x => x.ExecuteAsync(default));
}
