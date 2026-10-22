using Hangfire;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class ProcessEmailOutboxJob(
    IEmailOutboxProcessor outbox,
    IHumansMetrics metrics) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await outbox.ProcessQueuedAsync(cancellationToken);

        metrics.RecordJobRun("process_email_outbox", "success");
    }
}
