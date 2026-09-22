using Hangfire;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Jobs;

/// <summary>
/// Runs the email outbox drain every 1 minute via Hangfire. The queue semantics — the
/// pause check, the batch pick-up window, the transport call, the retry backoff and the
/// campaign-grant status mirror — live inside the Email section behind
/// <see cref="IEmailOutboxProcessor"/>; this job is the scheduler shim around it.
/// </summary>
/// <remarks>
/// The assembly a Hangfire job lives in is not load-bearing: <c>UseHumansRecurringJobs</c>
/// registers every job with <c>RecurringJob.AddOrUpdate&lt;T&gt;(id, …)</c>, which rewrites
/// the stored type string on every startup, so the job id is the stable key. It sits under
/// <c>Jobs/</c> because Shell names the concrete type at both its DI registration and its
/// recurring-job registration, and HUM0034 makes every other public type in a section
/// assembly an error.
/// </remarks>
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
