using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal static class ExpenseReportMapper
{
    internal static ExpenseReportDto ToDto(ExpenseReport r) => new()
    {
        Id = r.Id,
        SubmitterUserId = r.SubmitterUserId,
        BudgetCategoryId = r.BudgetCategoryId,
        BudgetYearId = r.BudgetYearId,
        Status = r.Status,
        Note = r.Note,
        PayeeName = r.PayeeName,
        PayeeIban = r.PayeeIban,
        Total = r.Total,
        MaxAmount = r.MaxAmount,
        SubmittedAt = r.SubmittedAt,
        CoordinatorEndorsedByUserId = r.CoordinatorEndorsedByUserId,
        CoordinatorEndorsedAt = r.CoordinatorEndorsedAt,
        ApprovedByUserId = r.ApprovedByUserId,
        ApprovedAt = r.ApprovedAt,
        LastRejectionReason = r.LastRejectionReason,
        LastRejectedByUserId = r.LastRejectedByUserId,
        LastRejectedAt = r.LastRejectedAt,
        HoldedDocId = r.HoldedDocId,
        HoldedContactId = r.HoldedContactId,
        HoldedSupplierAccountNum = r.HoldedSupplierAccountNum,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        Lines = r.Lines.Select(l => new ExpenseLineDto
        {
            Id = l.Id,
            ExpenseReportId = l.ExpenseReportId,
            Description = l.Description,
            Amount = l.Amount,
            LineType = l.LineType,
            AttachmentId = l.AttachmentId,
            Attachment = l.Attachment is null
                ? null
                : new ExpenseAttachmentDto
                {
                    Id = l.Attachment.Id,
                    OriginalFileName = l.Attachment.OriginalFileName,
                    Extension = l.Attachment.Extension,
                    ContentType = l.Attachment.ContentType,
                    SizeBytes = l.Attachment.SizeBytes,
                    UploadedByUserId = l.Attachment.UploadedByUserId,
                    UploadedAt = l.Attachment.UploadedAt,
                    HoldedUploadedAt = l.Attachment.HoldedUploadedAt
                },
            SortOrder = l.SortOrder
        }).ToList()
    };
}
