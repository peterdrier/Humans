using Humans.Base.Constants;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// The GDPR surface of design §18. Export is one slice per row type; erasure nulls
/// attribution everywhere and deletes membership rows, but keeps the content — comments,
/// log bodies, minutes and documents are the association's own record of what it decided
/// and why, under Art. 17(3)(b). The merge fold re-points every user-id column.
/// </summary>
internal sealed partial class WorkgroupService
{
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await repository.GetRowsForUserAsync(userId, ct);

        // Group names are the register's, not the row's: stitched here so no row type has to
        // carry a denormalized copy.
        var graph = await repository.GetGraphAsync(ct);
        var names = graph.Workgroups.ToDictionary(w => w.Id, w => w.Name);
        var documentGroups = graph.Workgroups
            .SelectMany(w => w.Documents.Select(d => (d.Id, Title: d.Title, GroupId: w.Id)))
            .ToDictionary(x => x.Id, x => (x.Title, x.GroupId));

        string NameOf(Guid workgroupId) => names.TryGetValue(workgroupId, out var name) ? name : "(unknown)";

        return
        [
            new UserDataSlice(GdprExportSections.WorkgroupMemberships, rows.Memberships
                .Select(m => new
                {
                    Workgroup = NameOf(m.WorkgroupId),
                    Role = m.Role.ToString(),
                    m.JoinedAt,
                    m.LeftAt
                })
                .ToList()),
            new UserDataSlice(GdprExportSections.WorkgroupLogEntries, rows.LogEntries
                .Select(e => new
                {
                    Workgroup = NameOf(e.WorkgroupId),
                    Kind = e.Kind.ToString(),
                    e.OccurredOn,
                    e.Title,
                    e.Body,
                    e.CreatedAt
                })
                .ToList()),
            new UserDataSlice(GdprExportSections.WorkgroupMeetings, rows.Meetings
                .Select(m => new
                {
                    Workgroup = NameOf(m.WorkgroupId),
                    m.Title,
                    m.StartUtc,
                    m.EndUtc,
                    m.Location,
                    m.IsPublic,
                    m.Minutes,
                    m.CreatedAt
                })
                .ToList()),
            new UserDataSlice(GdprExportSections.WorkgroupDocuments, rows.Documents
                .Select(d => new
                {
                    Workgroup = NameOf(d.WorkgroupId),
                    d.Title,
                    Kind = d.Kind.ToString(),
                    Status = d.Status.ToString(),
                    Authored = d.CreatedByUserId == userId,
                    Edited = d.UpdatedByUserId == userId,
                    d.CreatedAt,
                    d.UpdatedAt
                })
                .ToList()),
            new UserDataSlice(GdprExportSections.WorkgroupComments, rows.Comments
                .Select(c => new
                {
                    Workgroup = documentGroups.TryGetValue(c.DocumentId, out var doc)
                        ? NameOf(doc.GroupId)
                        : "(unknown)",
                    Document = doc.Title ?? "(unknown)",
                    c.Category,
                    c.Body,
                    Disposition = c.Disposition.ToString(),
                    c.Response,
                    Hidden = c.HiddenAt is not null,
                    c.HiddenReason,
                    c.CreatedAt
                })
                .ToList())
        ];
    }

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();

        // The Board needs to know before the row is gone: a group whose only coordinator has
        // just been erased has nobody named on the register (§18).
        var graph = await repository.GetGraphAsync(ct);
        var orphaned = graph.Workgroups
            .Where(w => w.Status == WorkgroupStatus.Active)
            .Select(ToInfo)
            .Where(w => w.CoordinatorUserIds() is [var only] && only == userId)
            .ToList();

        await repository.EraseUserAsync(userId, now, ct);

        foreach (var info in orphaned)
        {
            await notifications.SendToRoleAsync(NotificationSource.WorkgroupRegistrationPending,
                NotificationClass.Actionable, NotificationPriority.Normal,
                $"No coordinator: {info.Name}", RoleNames.Board,
                "The group's only coordinator was erased under GDPR. Name a coordinator from its members.",
                actionUrl: "/Workgroups/Admin", cancellationToken: ct);
        }
    }

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        repository.ReassignToUserAsync(mergedFromUserId, mergedToUserId, now, ct);
}
