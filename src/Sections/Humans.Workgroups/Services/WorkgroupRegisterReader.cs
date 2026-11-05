using Humans.Base.Interfaces;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupRegisterReader(IWorkgroupRepository repository) : IApplicationService
{
    public async Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default)
    {
        var graph = await repository.GetGraphAsync(ct);
        return graph.Workgroups.Select(ToInfo).ToList();
    }

    public async Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).FirstOrDefault(w => string.Equals(w.Slug, slug, StringComparison.Ordinal));

    public async Task<WorkgroupInfo?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var workgroup = await repository.GetWorkgroupAsync(id, ct);
        return workgroup is null ? null : ToInfo(workgroup);
    }

    public async Task<IReadOnlyList<WorkgroupInfo>> GetForMemberAsync(Guid userId, CancellationToken ct = default)
    {
        var register = await GetRegisterAsync(ct);
        return register.Where(w => w.IsMember(userId)).ToList();
    }

    internal static WorkgroupInfo ToInfo(Workgroup w) => new(
        w.Id,
        w.Slug,
        w.Name,
        w.Purpose,
        w.Deliverable,
        w.DeliverableKind,
        w.Audience,
        w.TargetDate,
        w.Status,
        w.DormantReason,
        w.Reasons,
        w.DriveFolderId,
        SafeHttpLink(w.DiscordChannelUrl),
        w.AppliedByUserId,
        w.AppliedAt,
        w.RegisteredAt,
        w.EndedAt,
        w.Members
            .Select(m => new WorkgroupMemberInfo(m.Id, m.UserId, m.Role, m.JoinedAt, m.LeftAt, m.IsPending == true))
            .ToList(),
        // Soft-deleted meetings are gone outside the repository; the log keeps the history.
        w.Meetings
            .Where(m => m.DeletedAt is null)
            .Select(m => new WorkgroupMeetingInfo(
                m.Id, m.Title, m.StartUtc, m.EndUtc, m.Location, SafeHttpLink(m.LocationUrl), m.IsPublic,
                m.Minutes, m.CreatedByUserId, m.CreatedAt, m.UpdatedAt, m.Description,
                m.RecurrenceRule, m.RecurrenceTimezone))
            .ToList(),
        w.LogEntries
            .Select(e => new WorkgroupLogEntryInfo(
                e.Id, e.Kind, e.OccurredOn, e.Title, e.Body, e.AuthorUserId, e.DocumentId,
                e.SurveyId, e.CreatedAt, e.UpdatedAt, e.MeetingId))
            .ToList(),
        w.Documents
            .Select(ToInfo)
            .ToList(),
        w.BudgetAmount,
        w.HoldedAccountNumber,
        w.HoldedAccountId,
        w.IsSensitive == true);

    internal static WorkgroupDocumentInfo ToInfo(WorkgroupDocument d) => new(
        d.Id,
        d.WorkgroupId,
        d.Title,
        d.Kind,
        d.Body,
        d.Status,
        d.CommentCategories.ToList(),
        d.CommentsOpenAt,
        d.CommentsCloseAt,
        d.DeliveredAt,
        d.Disposition,
        d.DispositionNote,
        d.DispositionAt,
        d.DispositionByUserId,
        d.CreatedByUserId,
        d.UpdatedByUserId,
        d.CreatedAt,
        d.UpdatedAt,
        d.Comments
            .Select(c => new WorkgroupCommentInfo(
                c.Id, c.DocumentId, c.Category, c.AuthorUserId, c.Body, c.CreatedAt,
                c.Disposition, c.Response, c.RespondedByUserId, c.RespondedAt, c.HiddenAt,
                c.HiddenByUserId, c.HiddenReason))
            .ToList());

    // Also guards stored legacy links when projecting them into an href.
    internal static string? SafeHttpLink(string? url)
    {
        var trimmed = url?.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" ? trimmed : null;
    }
}
