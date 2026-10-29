using Humans.Base.Extensions;
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
    internal const string WorkgroupApplications = "WorkgroupApplications";
    internal const string WorkgroupMemberships = "WorkgroupMemberships";
    internal const string WorkgroupLogEntries = "WorkgroupLogEntries";
    internal const string WorkgroupMeetings = "WorkgroupMeetings";
    internal const string WorkgroupDocuments = "WorkgroupDocuments";
    internal const string WorkgroupComments = "WorkgroupComments";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await repository.GetRowsForUserAsync(userId, ct);

        // Group names come from the register, so no row type carries a denormalized copy.
        var graph = await repository.GetGraphAsync(ct);
        var names = graph.Workgroups.ToDictionary(w => w.Id, w => w.Name);
        var documentGroups = graph.Workgroups
            .SelectMany(w => w.Documents.Select(d => (d.Id, d.Title, GroupId: w.Id)))
            .ToDictionary(x => x.Id, x => (x.Title, x.GroupId));

        string NameOf(Guid workgroupId) => names.TryGetValue(workgroupId, out var name) ? name : "(unknown)";

        return
        [
            // Erasure nulls the applicant attribution on the group row, so it is disclosed here too.
            new UserDataSlice(WorkgroupApplications, rows.AppliedFor
                .Select(w => new
                {
                    Workgroup = w.Name,
                    Status = w.Status.ToString(),
                    w.Purpose,
                    AppliedAt = w.AppliedAt.ToIso8601(),
                    RegisteredAt = w.RegisteredAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupMemberships, rows.Memberships
                .Select(m => new
                {
                    Workgroup = NameOf(m.WorkgroupId),
                    Role = m.Role.ToString(),
                    Pending = m.IsPending == true,
                    JoinedAt = m.JoinedAt.ToIso8601(),
                    LeftAt = m.LeftAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupLogEntries, rows.LogEntries
                .Select(e => new
                {
                    Workgroup = NameOf(e.WorkgroupId),
                    Kind = e.Kind.ToString(),
                    OccurredOn = e.OccurredOn.ToInvariantDate(),
                    e.Title,
                    e.Body,
                    CreatedAt = e.CreatedAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupMeetings, rows.Meetings
                .Select(m => new
                {
                    Workgroup = NameOf(m.WorkgroupId),
                    m.Title,
                    StartUtc = m.StartUtc.ToIso8601(),
                    EndUtc = m.EndUtc.ToIso8601(),
                    m.Location,
                    m.IsPublic,
                    m.Minutes,
                    CreatedAt = m.CreatedAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupDocuments, rows.Documents
                .Select(d => new
                {
                    Workgroup = NameOf(d.WorkgroupId),
                    d.Title,
                    Kind = d.Kind.ToString(),
                    Status = d.Status.ToString(),
                    Authored = d.CreatedByUserId == userId,
                    Edited = d.UpdatedByUserId == userId,
                    DispositionRecorded = d.DispositionByUserId == userId,
                    CreatedAt = d.CreatedAt.ToIso8601(),
                    UpdatedAt = d.UpdatedAt.ToIso8601()
                })
                .ToList()),
            new UserDataSlice(WorkgroupComments, rows.Comments
                .Select(c => new
                {
                    Workgroup = documentGroups.TryGetValue(c.DocumentId, out var doc)
                        ? NameOf(doc.GroupId)
                        : "(unknown)",
                    Document = doc.Title ?? "(unknown)",
                    c.Category,
                    c.Body,
                    // They may appear as responder or moderator on somebody else's comment.
                    Authored = c.AuthorUserId == userId,
                    Responded = c.RespondedByUserId == userId,
                    HiddenByThisPerson = c.HiddenByUserId == userId,
                    Disposition = c.Disposition.ToString(),
                    c.Response,
                    Hidden = c.HiddenAt is not null,
                    c.HiddenReason,
                    CreatedAt = c.CreatedAt.ToIso8601()
                })
                .ToList())
        ];
    }

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
            await NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupRegistrationPending,
                $"No coordinator: {info.Name}", info,
                "The group's only coordinator was erased under GDPR. Name a coordinator from its members.", ct);
        }
    }

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
}
