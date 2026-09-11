using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services;

/// <summary>
/// Documents and their comment period (design §12): Draft → Published → Delivered, the
/// categorised comment window any signed-in human may write into, and the group's
/// per-comment and per-category answers. Delivery freezes the body and starts the
/// Board's clock; the disposition itself is in the lifecycle partial.
/// </summary>
internal sealed partial class WorkgroupService
{
    // ── Documents ─────────────────────────────────────────────────────────

    public async Task<Guid> CreateDocumentAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroup = await RequireAsync(workgroupId, ct);
        RequireAcceptsMemberWork(workgroup);
        if (string.IsNullOrWhiteSpace(save.Title))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NameRequired);

        var now = clock.GetCurrentInstant();
        var document = new WorkgroupDocument
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            Title = save.Title.Trim(),
            Kind = save.Kind,
            Body = save.Body ?? string.Empty,
            Status = WorkgroupDocumentStatus.Draft,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.AddDocumentAsync(document, ct);
        return document.Id;
    }

    public async Task UpdateDocumentAsync(
        Guid documentId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        RequireAcceptsMemberWork(workgroup);
        if (string.IsNullOrWhiteSpace(save.Title))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NameRequired);
        // Delivered is the association's copy of what it received; the body stops moving.
        if (document.Status == WorkgroupDocumentStatus.Delivered)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.DocumentFrozen);

        document.Title = save.Title.Trim();
        document.Kind = save.Kind;
        document.Body = save.Body ?? string.Empty;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateDocumentAsync(document, ct);
    }

    public async Task PublishDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        RequireAcceptsMemberWork(workgroup);
        if (document.Status != WorkgroupDocumentStatus.Draft)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.WrongStatus);
        if (string.IsNullOrWhiteSpace(document.Body))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.BodyRequired);

        var now = clock.GetCurrentInstant();
        document.Status = WorkgroupDocumentStatus.Published;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.DocumentPublished, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);

        var info = ToInfo(workgroup);
        await NotifyAsync(info.CurrentMemberUserIds(), NotificationSource.WorkgroupDocumentActivity,
            $"Document published: {document.Title}", info,
            $"{workgroup.Name} published {document.Title}.", ct);
    }

    public async Task OpenCommentsAsync(
        Guid documentId, Guid actorUserId, WorkgroupCommentWindow window, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        RequireAcceptsMemberWork(workgroup);
        // A draft has nothing to comment on and a delivered document is past comment.
        if (document.Status != WorkgroupDocumentStatus.Published)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NotPublished);

        var categories = window.Categories
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (categories.Count == 0)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.CategoriesRequired);
        if (window.ClosesAt <= window.OpensAt)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.WindowInvalid);

        var now = clock.GetCurrentInstant();
        document.CommentCategories = categories;
        document.CommentsOpenAt = window.OpensAt;
        document.CommentsCloseAt = window.ClosesAt;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CommentPeriodOpened, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);

        var info = ToInfo(workgroup);
        await NotifyAsync(info.CurrentMemberUserIds(), NotificationSource.WorkgroupDocumentActivity,
            $"Comment period open: {document.Title}", info,
            $"Comments on {document.Title} are open until {window.ClosesAt.InUtc().Date.ToInvariantLongDate()}.", ct);
    }

    public async Task CloseCommentsAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        RequireAcceptsMemberWork(workgroup);
        if (document.CommentsOpenAt is null)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.WrongStatus);

        var now = clock.GetCurrentInstant();
        // Closing early is just moving the end of the window to now; the comments stay.
        document.CommentsCloseAt = now;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CommentPeriodClosed, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);
    }

    public async Task DeliverDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        RequireAcceptsMemberWork(workgroup);
        if (document.Status != WorkgroupDocumentStatus.Published)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.NotPublished);

        var now = clock.GetCurrentInstant();
        document.Status = WorkgroupDocumentStatus.Delivered;
        document.DeliveredAt = now;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Delivered, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(NotificationSource.WorkgroupDocumentActivity,
            $"Delivered for a decision: {document.Title}", info,
            $"{workgroup.Name} delivered {document.Title} to the {workgroup.Audience}.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Delivered, info, document.Title, ct);
    }

    // ── Comments ──────────────────────────────────────────────────────────

    public async Task<Guid> AddCommentAsync(
        Guid documentId, Guid actorUserId, string category, string body, CancellationToken ct = default)
    {
        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        var now = clock.GetCurrentInstant();

        // Dormant freezes every member mutation, comments included (design §5) — Close and
        // Withdraw leave an open window alone, so the group's status has to be checked here.
        RequireAcceptsMemberWork(workgroup);

        // Beyond that the window is the only gate: any signed-in human may comment while it is
        // open, whatever the group's own membership says.
        if (!ToInfo(document).IsOpenForComment(now))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.CommentsClosed);

        var matched = document.CommentCategories
            .FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.UnknownCategory);
        if (string.IsNullOrWhiteSpace(body))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.BodyRequired);

        var comment = new WorkgroupDocumentComment
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            Category = matched,
            AuthorUserId = actorUserId,
            Body = body.Trim(),
            Disposition = WorkgroupCommentDisposition.Pending,
            CreatedAt = now
        };
        await repository.AddCommentAsync(comment, ct);
        return comment.Id;
    }

    public async Task RespondToCommentAsync(
        Guid commentId,
        Guid actorUserId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        var comment = await repository.GetCommentAsync(commentId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);
        var workgroup = await RequireAsync(comment.Document.WorkgroupId, ct);
        // Responding is the answering half of the resolution's "show what you heard"; it
        // stays open after the window closes, but not after the group ends.
        RequireAcceptsMemberWork(workgroup);

        var now = clock.GetCurrentInstant();
        Respond(comment, disposition, response, actorUserId, now);
        await repository.UpdateCommentsAsync([comment], ct);

        if (comment.AuthorUserId is { } author)
        {
            var info = ToInfo(workgroup);
            await NotifyAsync([author], NotificationSource.WorkgroupDocumentActivity,
                $"Your comment was answered: {comment.Document.Title}", info,
                Trimmed(response) ?? $"The group marked your comment {disposition}.", ct);
        }
    }

    public async Task RespondToCategoryAsync(
        Guid documentId,
        Guid actorUserId,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        var (workgroup, document) = await RequireDocumentAsync(documentId, ct);
        RequireAcceptsMemberWork(workgroup);

        var matched = document.CommentCategories
            .FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.UnknownCategory);

        // Only still-Pending comments: a comment already answered individually keeps its
        // own answer rather than being overwritten by the bulk pass.
        var pending = document.Comments
            .Where(c => c.Disposition == WorkgroupCommentDisposition.Pending
                && c.HiddenAt is null
                && string.Equals(c.Category, matched, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pending.Count == 0)
            return;

        var now = clock.GetCurrentInstant();
        foreach (var comment in pending)
            Respond(comment, disposition, response, actorUserId, now);
        await repository.UpdateCommentsAsync(pending, ct);

        var info = ToInfo(workgroup);
        var authors = pending.Select(c => c.AuthorUserId).OfType<Guid>().Distinct().ToList();
        await NotifyAsync(authors, NotificationSource.WorkgroupDocumentActivity,
            $"Your comment was answered: {document.Title}", info,
            Trimmed(response) ?? $"The group marked the {matched} comments {disposition}.", ct);
    }

    public async Task HideCommentAsync(
        Guid commentId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new WorkgroupRuleException(WorkgroupErrorKeys.HideReasonRequired);

        var comment = await repository.GetCommentAsync(commentId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);
        var workgroup = await RequireAsync(comment.Document.WorkgroupId, ct);
        RequireAcceptsMemberWork(workgroup);

        comment.HiddenAt = clock.GetCurrentInstant();
        comment.HiddenByUserId = actorUserId;
        comment.HiddenReason = reason.Trim();
        await repository.UpdateCommentsAsync([comment], ct);

        // Moderation is the one member action the Board needs to be able to review, so the
        // reason lives in the audit trail as well as on the row.
        await AuditAsync(AuditAction.WorkgroupCommentHidden, workgroup,
            $"Hid a comment on {comment.Document.Title}: {reason.Trim()}", actorUserId,
            AuditEntityTypes.WorkgroupComment, comment.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void Respond(
        WorkgroupDocumentComment comment,
        WorkgroupCommentDisposition disposition,
        string? response,
        Guid actorUserId,
        NodaTime.Instant now)
    {
        comment.Disposition = disposition;
        comment.Response = Trimmed(response);
        comment.RespondedByUserId = actorUserId;
        comment.RespondedAt = now;
    }

    private async Task<(Workgroup Workgroup, WorkgroupDocument Document)> RequireDocumentAsync(
        Guid documentId, CancellationToken ct)
    {
        var document = await repository.GetDocumentAsync(documentId, ct)
            ?? throw new WorkgroupRuleException(WorkgroupErrorKeys.NotFound);
        var workgroup = await RequireAsync(document.WorkgroupId, ct);
        return (workgroup, document);
    }
}
