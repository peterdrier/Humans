using Hangfire;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Jobs;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Hangfire-backed implementation of <see cref="IImmediateOutboxProcessor"/>.
/// Enqueues a one-off <see cref="ProcessEmailOutboxJob"/> run in addition to
/// the recurring 1-minute schedule so time-sensitive templates (email
/// verification, magic-link, workspace credentials) are delivered
/// immediately.
/// </summary>
public sealed class HangfireImmediateOutboxProcessor : IImmediateOutboxProcessor
{
    private readonly IBackgroundJobClient _backgroundJobClient;

    public HangfireImmediateOutboxProcessor(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient;
    }

    public void TriggerImmediate()
    {
        _backgroundJobClient.Enqueue<ProcessEmailOutboxJob>(x => x.ExecuteAsync(default));
    }
}
