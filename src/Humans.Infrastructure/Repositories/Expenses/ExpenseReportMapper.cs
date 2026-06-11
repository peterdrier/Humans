using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Repositories.Expenses;

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
        SubmittedAt = r.SubmittedAt,
        CoordinatorEndorsedByUserId = r.CoordinatorEndorsedByUserId,
        CoordinatorEndorsedAt = r.CoordinatorEndorsedAt,
        ApprovedByUserId = r.ApprovedByUserId,
        ApprovedAt = r.ApprovedAt,
        SepaSentAt = r.SepaSentAt,
        PaidAt = r.PaidAt,
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
                    UploadedAt = l.Attachment.UploadedAt
                },
            SortOrder = l.SortOrder
        }).ToList()
    };
}
