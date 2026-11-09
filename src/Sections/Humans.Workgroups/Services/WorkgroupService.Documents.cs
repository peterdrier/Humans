using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services;

internal sealed partial class WorkgroupService
{
    public Task<WorkgroupMutationResult<Guid>> CreateDocumentAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default) =>
        DocumentWorkflow.CreateDocumentAsync(workgroupId, actorUserId, save, ct);

    public Task<WorkgroupMutationResult> UpdateDocumentAsync(
        Guid documentId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default) =>
        DocumentWorkflow.UpdateDocumentAsync(documentId, actorUserId, save, ct);

    public Task<WorkgroupMutationResult> PublishDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        DocumentWorkflow.PublishDocumentAsync(documentId, actorUserId, ct);

    public Task<WorkgroupMutationResult> OpenCommentsAsync(
        Guid documentId, Guid actorUserId, WorkgroupCommentWindow window, CancellationToken ct = default) =>
        DocumentWorkflow.OpenCommentsAsync(documentId, actorUserId, window, ct);

    public Task<WorkgroupMutationResult> CloseCommentsAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        DocumentWorkflow.CloseCommentsAsync(documentId, actorUserId, ct);

    public Task<WorkgroupMutationResult> DeliverDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        DocumentWorkflow.DeliverDocumentAsync(documentId, actorUserId, ct);

    public Task<WorkgroupMutationResult<Guid>> AddCommentAsync(
        Guid documentId, Guid actorUserId, string category, string body, CancellationToken ct = default) =>
        DocumentWorkflow.AddCommentAsync(documentId, actorUserId, category, body, ct);

    public Task<WorkgroupMutationResult> RespondToCommentAsync(
        Guid commentId,
        Guid actorUserId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default) =>
        DocumentWorkflow.RespondToCommentAsync(commentId, actorUserId, disposition, response, ct);

    public Task<WorkgroupMutationResult> RespondToCategoryAsync(
        Guid documentId,
        Guid actorUserId,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default) =>
        DocumentWorkflow.RespondToCategoryAsync(documentId, actorUserId, category, disposition, response, ct);

    public Task<WorkgroupMutationResult> HideCommentAsync(
        Guid commentId, Guid actorUserId, string reason, CancellationToken ct = default) =>
        DocumentWorkflow.HideCommentAsync(commentId, actorUserId, reason, ct);
}
