using Hangfire;
using Humans.Application.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Jobs;

/// <summary>
/// Runs the email outbox drain every 1 minute via Hangfire. The queue semantics — the
/// pause check, the batch pick-up window, the transport call, the retry backoff and the
/// campaign-grant status mirror — live inside the Email section behind
/// <see cref="IEmailOutboxProcessor"/>; this job is the scheduler shim around it.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-1
/// (nobodies-collective/Humans#866). The "Hangfire pins a job to its assembly" claim that
/// kept it in Base was re-measured and is false: <c>UseHumansRecurringJobs</c> registers
/// every job with <c>RecurringJob.AddOrUpdate&lt;T&gt;(id, …)</c>, and <c>AddOrUpdate</c>
/// rewrites the stored type string on every startup, so the job id — not the assembly —
/// is the stable key. It sits under <c>Jobs/</c> because Shell names the concrete
/// type at both its DI registration and its recurring-job registration, and HUM0034 makes
/// every other public type in a section assembly an error.
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
