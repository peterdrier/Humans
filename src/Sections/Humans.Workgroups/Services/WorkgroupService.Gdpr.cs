using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// Erasure keeps the content: comments, log bodies, minutes and documents are the association's record of what it
/// decided and why, under Art. 17(3)(b).
/// </summary>
internal sealed partial class WorkgroupService
{
    internal const string WorkgroupApplications = WorkgroupDataExport.WorkgroupApplications;
    internal const string WorkgroupMemberships = WorkgroupDataExport.WorkgroupMemberships;
    internal const string WorkgroupLogEntries = WorkgroupDataExport.WorkgroupLogEntries;
    internal const string WorkgroupMeetings = WorkgroupDataExport.WorkgroupMeetings;
    internal const string WorkgroupDocuments = WorkgroupDataExport.WorkgroupDocuments;
    internal const string WorkgroupComments = WorkgroupDataExport.WorkgroupComments;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();

        // Read before the row is gone: the Board must know when a group's only coordinator is erased.
        var graph = await repository.GetGraphAsync(ct);
        var orphaned = graph.Workgroups
            .Where(w => w.Status == WorkgroupStatus.Active)
            .Select(ToInfo)
            .Where(w => w.CoordinatorUserIds() is [var only] && only == userId)
            .ToList();

        await repository.EraseUserAsync(userId, now, ct);

        foreach (var info in orphaned)
        {
            await notices.NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
                $"No coordinator: {info.Name}", info,
                "The group's only coordinator was erased under GDPR. Name a coordinator from its members.", ct);
        }
    }

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
}
