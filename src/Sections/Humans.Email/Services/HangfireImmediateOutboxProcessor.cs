using Hangfire;
using Humans.Email.Jobs;

namespace Humans.Email.Services;

internal sealed class HangfireImmediateOutboxProcessor(IBackgroundJobClient backgroundJobClient)
    : IImmediateOutboxProcessor
{
    public void TriggerImmediate() =>
        backgroundJobClient.Enqueue<ProcessEmailOutboxJob>(x => x.ExecuteAsync(default));
}
