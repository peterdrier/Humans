using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services.Dtos;
using NodaTime;

namespace Humans.Expenses.Services;

internal sealed class ExpenseAttachments(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger logger) : IApplicationService
{
    internal static string AttachmentKey(Guid id, string extension) =>
        $"uploads/expense-attachments/{id}{extension}";

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".heic"
        };

    internal async Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default)
    {
        var reportId = await repo.GetReportIdByAttachmentIdAsync(attachmentId, ct);
        if (reportId is null) return null;
        return await repo.GetByIdAsync(reportId.Value, ct);
    }

    internal async Task<ExpenseAttachmentDownload?> TryReadAttachmentAsync(
        ExpenseReportDto owningReport,
        Guid attachmentId,
        CancellationToken ct = default)
    {
        var attachment = owningReport.Lines
            .FirstOrDefault(l => l.Attachment?.Id == attachmentId)?
            .Attachment;
        if (attachment is null) return null;

        var bytes = await fileStorage.TryReadAsync(
            AttachmentKey(attachment.Id, attachment.Extension), ct);
        return bytes is null
            ? null
            : new ExpenseAttachmentDownload(bytes, attachment.ContentType, attachment.OriginalFileName);
    }

    internal async Task CleanupUnlinkedAttachmentsAsync(
        IEnumerable<(Guid Id, string Extension)> attachments, Guid lineId)
    {
        foreach (var attachment in attachments.Distinct())
        {
            try
            {
                // A save can commit before its acknowledgement or audit fails. Verify the actual linkage
                // without caller cancellation; a failed verification retains the file for safety.
                if (await repo.GetReportIdByAttachmentIdAsync(attachment.Id, CancellationToken.None) is not null)
                    continue;
                await fileStorage.DeleteAsync(AttachmentKey(attachment.Id, attachment.Extension), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not verify or delete attachment file {AttachmentId} for line {LineId}",
                    attachment.Id, lineId);
            }
        }
    }

    private const long AttachmentMaxBytes = 20 * 1024 * 1024;
    private const int AttachmentFileNameMaxLength = 255;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/jpg", "image/png", "image/heic"
    };

    internal static (string? Extension, ExpenseError? Refusal) ValidateAttachmentUpload(
        string originalFileName, string contentType, Stream content)
    {
        if (content is null || content.Length == 0)
            return (null, ExpenseMutationResult.Failure("Expenses_Flash_SelectFile").Error);
        if (content.Length > AttachmentMaxBytes)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_FileTooLarge", AttachmentMaxBytes / (1024 * 1024)).Error);

        var fileName = Path.GetFileName(originalFileName);
        if (fileName.Length > AttachmentFileNameMaxLength)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_FilenameTooLong", AttachmentFileNameMaxLength).Error);

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedContentTypes.Contains(contentType) || !AllowedExtensions.Contains(extension))
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_UnsupportedFileType").Error);

        return (extension, null);
    }

    internal async Task<ExpenseError?> StoreAttachmentAsync(
        ExpenseReportAccess access, Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default)
    {
        var (fileExtension, uploadRefusal) = ValidateAttachmentUpload(originalFileName, contentType, content);
        if (uploadRefusal is not null) return uploadRefusal;
        var extension = fileExtension!;
        var (editableReport, refusal) = await access.GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
        if (refusal is not null) return refusal.Error;
        var report = editableReport!;

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return new("Expenses_Validation_LineNotOnReport", []);
        var previousAttachment = line.Attachment is { } previous
            ? new ExpenseAttachment
            {
                Id = previous.Id,
                OriginalFileName = previous.OriginalFileName,
                Extension = previous.Extension,
                ContentType = previous.ContentType,
                SizeBytes = previous.SizeBytes,
                UploadedByUserId = previous.UploadedByUserId,
                UploadedAt = previous.UploadedAt,
                HoldedUploadedAt = previous.HoldedUploadedAt,
            }
            : null;

        var attachmentId = Guid.NewGuid();
        await fileStorage.SaveAsync(AttachmentKey(attachmentId, extension), content, ct);

        var attachment = new ExpenseAttachment
        {
            Id = attachmentId,
            OriginalFileName = Path.GetFileName(originalFileName),
            Extension = extension,
            ContentType = contentType,
            SizeBytes = content.Length,
            // The uploader, not the owner: an admin filing on a member's behalf uploads.
            UploadedByUserId = actorUserId,
            UploadedAt = clock.GetCurrentInstant()
        };
        ExpenseAttachment? replacedAttachment = null;
        try
        {
            var replacement = await repo.ReplaceLineAttachmentAsync(reportId, lineId, attachment, ct,
                declineState: line.Declined ? (clock.GetCurrentInstant(), null, null, null) : null);
            if (!replacement.Found)
            {
                // The line disappeared before the save; no metadata was written.
                try
                {
                    await fileStorage.DeleteAsync(AttachmentKey(attachmentId, extension), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not delete unlinked attachment file {AttachmentId}", attachmentId);
                }
                return new("Expenses_Validation_LineNotOnReport", []);
            }
            replacedAttachment = replacement.Replaced;

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseAttachmentUploaded,
                AuditEntityTypes.Report, reportId,
                $"Attachment uploaded to line {lineId}.",
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditSubjectTypes.User);
        }
        catch
        {
            try
            {
                // Restore metadata, linkage and any decline/total change atomically, including when the failed
                // save committed before throwing. Keep the old file until audit succeeds.
                await repo.ReplaceLineAttachmentAsync(
                    reportId, lineId, previousAttachment, CancellationToken.None,
                    declineState: line.Declined
                        ? (report.UpdatedAt, line.DeclinedReason, line.DeclinedByUserId, line.DeclinedAt)
                        : null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not roll back attachment metadata {AttachmentId} for line {LineId}",
                    attachmentId, lineId);
            }

            await CleanupUnlinkedAttachmentsAsync([(attachmentId, extension)], lineId);

            throw;
        }

        if (replacedAttachment is not null)
        {
            try
            {
                await fileStorage.DeleteAsync(
                    AttachmentKey(replacedAttachment.Id, replacedAttachment.Extension), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not delete superseded attachment file {AttachmentId}", replacedAttachment.Id);
            }
        }

        return null;
    }

    internal async Task<ExpenseMutationResult> RemoveAsync(
        ExpenseReportAccess access, Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, CancellationToken ct)
    {
        var (editableReport, refusal) = await access.GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
        if (refusal is not null) return refusal;
        var report = editableReport!;

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport");

        if (line.Attachment is null) return ExpenseMutationResult.Success; // idempotent

        ExpenseAttachment? removed = null;
        try
        {
            removed = await repo.RemoveLineAttachmentAsync(reportId, lineId, ct);
            if (removed is null) return ExpenseMutationResult.Success;

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseAttachmentRemoved,
                AuditEntityTypes.Report, reportId,
                $"Attachment removed from line {lineId}.",
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditSubjectTypes.User);
            return ExpenseMutationResult.Success;
        }
        finally
        {
            await CleanupUnlinkedAttachmentsAsync(
                [(removed?.Id ?? line.Attachment.Id, removed?.Extension ?? line.Attachment.Extension)], lineId);
        }
    }
}
