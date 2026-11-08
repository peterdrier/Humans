using Humans.Base.Interfaces;
using Humans.Preapprovals.Services;

namespace Humans.Preapprovals.Jobs;

/// <summary>The reminder component owns selection and delivery. It sends and records; it never changes a grant.</summary>
internal sealed class PreapprovalRemindersJob(PreapprovalDeadlineReminders reminders) : IRecurringJob
{
    /// <summary>Hangfire's stored job id: renaming it orphans the schedule.</summary>
    public const string JobId = "preapprovals-reminders";

    public Task ExecuteAsync(CancellationToken ct = default) => reminders.SendDeadlineRemindersAsync(ct);
}
