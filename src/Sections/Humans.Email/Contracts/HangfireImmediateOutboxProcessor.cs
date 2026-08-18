using Hangfire;
using Humans.Email.Jobs;

namespace Humans.Email.Contracts;

/// <summary>
/// Hangfire-backed implementation of <see cref="IImmediateOutboxProcessor"/>.
/// Enqueues a one-off <see cref="ProcessEmailOutboxJob"/> run in addition to
/// the recurring 1-minute schedule so time-sensitive templates (email
/// verification, magic-link, workspace credentials) are delivered
/// immediately.
/// </summary>
/// <remarks>
/// Followed <see cref="ProcessEmailOutboxJob"/> out of <c>Humans.Infrastructure</c> at
/// G5 lane 5b-1 (nobodies-collective/Humans#866): it names the job's concrete type, and
/// Base cannot reference <c>Humans.Email</c> without a cycle. Shell registers it, so it
/// is public under <c>Contracts/</c>; the job it enqueues is public under <c>Jobs/</c>.
/// </remarks>
public sealed class HangfireImmediateOutboxProcessor(IBackgroundJobClient backgroundJobClient)
    : IImmediateOutboxProcessor
{
    public void TriggerImmediate() =>
        backgroundJobClient.Enqueue<ProcessEmailOutboxJob>(x => x.ExecuteAsync(default));
}
