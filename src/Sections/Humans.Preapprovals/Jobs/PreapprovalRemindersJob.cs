using Humans.Base.Interfaces;
using Humans.Preapprovals.Services;

namespace Humans.Preapprovals.Jobs;

/// <summary>The rules live in the service, where tests reach them. It sends and records; it never changes a grant.</summary>
internal sealed class PreapprovalRemindersJob(IPreapprovalManagement preapprovals) : IRecurringJob
{
    /// <summary>Hangfire's stored job id: renaming it orphans the schedule.</summary>
    public const string JobId = "preapprovals-reminders";

    public Task ExecuteAsync(CancellationToken ct = default) => preapprovals.SendDeadlineRemindersAsync(ct);
}
