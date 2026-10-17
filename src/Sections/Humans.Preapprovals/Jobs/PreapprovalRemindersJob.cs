using Humans.Base.Interfaces;
using Humans.Preapprovals.Services;

namespace Humans.Preapprovals.Jobs;

/// <summary>
/// The nightly deadline-reminder pass. One Hangfire job, one service call: the rules live in
/// <see cref="IPreapprovalManagement.SendDeadlineRemindersAsync"/> where tests reach
/// them. It sends and records; it never changes a grant.
/// </summary>
internal sealed class PreapprovalRemindersJob(IPreapprovalManagement preapprovals) : IRecurringJob
{
    /// <summary>Hangfire's stored job id. Renaming it orphans the schedule, so it never changes.</summary>
    public const string JobId = "preapprovals-reminders";

    public Task ExecuteAsync(CancellationToken ct = default) => preapprovals.SendDeadlineRemindersAsync(ct);
}
