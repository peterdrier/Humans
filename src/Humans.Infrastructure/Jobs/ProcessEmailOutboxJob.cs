using Hangfire;
using Humans.Application.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Runs the email outbox drain every 1 minute via Hangfire. The queue semantics — the
/// pause check, the batch pick-up window, the transport call, the retry backoff and the
/// campaign-grant status mirror — live inside the Email section behind
/// <see cref="IEmailOutboxProcessor"/>; this job is the scheduler shim around it.
/// </summary>
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
