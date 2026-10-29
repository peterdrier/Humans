using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services;

internal sealed partial class WorkgroupService
{
    /// <summary>Matches <c>WorkgroupDocumentComment.Category</c>, which every comment in an opened window must fit.</summary>
    private const int MaxCommentCategoryLength = 100;

    public async Task<WorkgroupMutationResult<Guid>> CreateDocumentAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var workgroupResult = await RequireActiveAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (string.IsNullOrWhiteSpace(save.Title))
            return new(Refusal: new(WorkgroupErrorKeys.NameRequired));

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
        return new(document.Id);
    }

    public async Task<WorkgroupMutationResult> UpdateDocumentAsync(
        Guid documentId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (string.IsNullOrWhiteSpace(save.Title))
            return new(Refusal: new(WorkgroupErrorKeys.NameRequired));
        // Delivered is the association's copy of what it received; the body stops moving.
        if (document.Status == WorkgroupDocumentStatus.Delivered)
            return new(Refusal: new(WorkgroupErrorKeys.DocumentFrozen));

        document.Title = save.Title.Trim();
        document.Kind = save.Kind;
        document.Body = save.Body ?? string.Empty;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateDocumentAsync(document, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> PublishDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (document.Status != WorkgroupDocumentStatus.Draft)
            return new(Refusal: new(WorkgroupErrorKeys.WrongStatus));
        if (string.IsNullOrWhiteSpace(document.Body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));

        var now = clock.GetCurrentInstant();
        document.Status = WorkgroupDocumentStatus.Published;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.DocumentPublished, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);

        var info = ToInfo(workgroup);
        await NotifyAsync(info.CurrentMemberUserIds(), WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            "Enum_WorkgroupLogKind_DocumentPublished", info, document.Title, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> OpenCommentsAsync(
        Guid documentId, Guid actorUserId, WorkgroupCommentWindow window, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (document.Status != WorkgroupDocumentStatus.Published)
            return new(Refusal: new(WorkgroupErrorKeys.NotPublished));

        var categories = window.Categories
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (categories.Count == 0)
            return new(Refusal: new(WorkgroupErrorKeys.CategoriesRequired));
        // A longer category would open a window whose own comments could never be saved.
        if (categories.Any(c => c.Length > MaxCommentCategoryLength))
            return new(Refusal: new(WorkgroupErrorKeys.CategoryTooLong));
        if (window.ClosesAt <= window.OpensAt)
            return new(Refusal: new(WorkgroupErrorKeys.WindowInvalid));

        var now = clock.GetCurrentInstant();
        document.CommentCategories = categories;
        document.CommentsOpenAt = window.OpensAt;
        document.CommentsCloseAt = window.ClosesAt;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CommentPeriodOpened, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);

        var info = ToInfo(workgroup);
        await NotifyAsync(info.CurrentMemberUserIds(), WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            "Enum_WorkgroupLogKind_CommentPeriodOpened", info, document.Title, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> CloseCommentsAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (document.CommentsOpenAt is null)
            return new(Refusal: new(WorkgroupErrorKeys.WrongStatus));

        var now = clock.GetCurrentInstant();
        document.CommentsCloseAt = now;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.CommentPeriodClosed, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);
        return new();
    }

    public async Task<WorkgroupMutationResult> DeliverDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);
        if (document.Status != WorkgroupDocumentStatus.Published)
            return new(Refusal: new(WorkgroupErrorKeys.NotPublished));

        var now = clock.GetCurrentInstant();
        // Delivering mid-window would cut short a publicly promised comment period.
        if (document.CommentsCloseAt is { } closes && closes > now)
            return new(Refusal: new(WorkgroupErrorKeys.CommentsStillOpen));

        document.Status = WorkgroupDocumentStatus.Delivered;
        document.DeliveredAt = now;
        document.UpdatedByUserId = actorUserId;
        document.UpdatedAt = now;
        await repository.UpdateDocumentAsync(document, ct);
        ct = CancellationToken.None;

        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.Delivered, now,
            document.Title, ct, authorUserId: actorUserId, documentId: document.Id);

        var info = ToInfo(workgroup);
        await NotifyBoardAsync(WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            $"Delivered for a decision: {document.Title}", info,
            $"{workgroup.Name} delivered {document.Title} to the {workgroup.Audience}.", ct);
        await EmailBoardAsync(WorkgroupNoticeKind.Delivered, info, document.Title, ct);
        return new();
    }

    public async Task<WorkgroupMutationResult<Guid>> AddCommentAsync(
        Guid documentId, Guid actorUserId, string category, string body, CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        var now = clock.GetCurrentInstant();

        // Close and Withdraw leave an open window alone, so the group's status must be checked here too.
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);

        if (workgroup.IsSensitive == true && !workgroup.Members.Any(m => m.UserId == actorUserId && m.LeftAt is null && m.IsPending != true))
            return new(Refusal: new(WorkgroupErrorKeys.MembersOnly));

        // Beyond that the window is the only gate: membership doesn't matter.
        if (!ToInfo(document).IsOpenForComment(now))
            return new(Refusal: new(WorkgroupErrorKeys.CommentsClosed));

        var matched = document.CommentCategories
            .FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is null) return new(Refusal: new(WorkgroupErrorKeys.UnknownCategory));
        if (string.IsNullOrWhiteSpace(body))
            return new(Refusal: new(WorkgroupErrorKeys.BodyRequired));
        if (body.Trim().Length > 4000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 4000));

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
        return new(comment.Id);
    }

    public async Task<WorkgroupMutationResult> RespondToCommentAsync(
        Guid commentId,
        Guid actorUserId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        var comment = await repository.GetCommentAsync(commentId, ct);
        if (comment is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        // Responding stays open after the window closes, but not after the group ends.
        var workgroupResult = await RequireActiveAsync(comment.Document.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        if (response?.Trim().Length is > 4000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 4000));

        var now = clock.GetCurrentInstant();
        Respond(comment, disposition, response, actorUserId, now);
        await repository.UpdateCommentsAsync([comment], ct);
        ct = CancellationToken.None;

        if (comment.AuthorUserId is { } author)
        {
            var info = ToInfo(workgroup);
            await NotifyAsync([author], WorkgroupsNotificationSources.WorkgroupDocumentActivity,
                "Workgroups_CommentAnswered", info, $"{comment.Document.Title}: {Trimmed(response)}", ct);
        }
        return new();
    }

    public async Task<WorkgroupMutationResult> RespondToCategoryAsync(
        Guid documentId,
        Guid actorUserId,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        var documentResult = await RequireDocumentAsync(documentId, ct);
        if (documentResult.Refusal is { } documentRefusal) return new(Refusal: documentRefusal);
        var (workgroup, document) = documentResult.Value;
        if (RequireAcceptsMemberWork(workgroup) is { } stateRefusal) return new(Refusal: stateRefusal);

        var matched = document.CommentCategories
            .FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is null) return new(Refusal: new(WorkgroupErrorKeys.UnknownCategory));

        // Only still-Pending comments: one answered individually keeps its own answer.
        var pending = document.Comments
            .Where(c => c.Disposition == WorkgroupCommentDisposition.Pending
                && c.HiddenAt is null
                && string.Equals(c.Category, matched, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pending.Count == 0)
            return new();
        if (response?.Trim().Length is > 4000)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 4000));

        var now = clock.GetCurrentInstant();
        foreach (var comment in pending)
            Respond(comment, disposition, response, actorUserId, now);
        await repository.UpdateCommentsAsync(pending, ct);
        ct = CancellationToken.None;

        var info = ToInfo(workgroup);
        var authors = pending.Select(c => c.AuthorUserId).OfType<Guid>().Distinct().ToList();
        await NotifyAsync(authors, WorkgroupsNotificationSources.WorkgroupDocumentActivity,
            "Workgroups_CommentAnswered", info, $"{document.Title}: {Trimmed(response)}", ct);
        return new();
    }

    public async Task<WorkgroupMutationResult> HideCommentAsync(
        Guid commentId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new(Refusal: new(WorkgroupErrorKeys.HideReasonRequired));

        var comment = await repository.GetCommentAsync(commentId, ct);
        if (comment is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroupResult = await RequireActiveAsync(comment.Document.WorkgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;

        if (reason.Trim().Length > 500)
            return new(Refusal: new(WorkgroupErrorKeys.TextTooLong, 500));

        comment.HiddenAt = clock.GetCurrentInstant();
        comment.HiddenByUserId = actorUserId;
        comment.HiddenReason = reason.Trim();
        await repository.UpdateCommentsAsync([comment], ct);

        // Audited so the Board can review moderation; the row shows only "hidden by the group".
        await AuditAsync(AuditAction.WorkgroupCommentHidden, workgroup,
            $"Hid a comment on {comment.Document.Title}: {reason.Trim()}", actorUserId,
            AuditEntityTypes.WorkgroupComment, comment.Id);
        return new();
    }

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

    private async Task<WorkgroupMutationResult<(Workgroup Workgroup, WorkgroupDocument Document)>> RequireDocumentAsync(
        Guid documentId, CancellationToken ct)
    {
        var document = await repository.GetDocumentAsync(documentId, ct);
        if (document is null) return new(Refusal: new(WorkgroupErrorKeys.NotFound));
        var workgroup = await RequireAsync(document.WorkgroupId, ct);
        return workgroup.Refusal is { } refusal
            ? new(Refusal: refusal)
            : new((workgroup.Value!, document));
    }
}
