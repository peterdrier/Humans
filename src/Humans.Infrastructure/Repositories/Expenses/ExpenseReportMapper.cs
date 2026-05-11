using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Repositories.Expenses;

/// <summary>
/// Static projection helpers that map EF entities to DTOs.
/// Lives in Infrastructure (which owns EF) so the Application layer stays clean.
/// </summary>
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
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        Lines = r.Lines.Select(ToLineDto).ToList()
    };

    internal static ExpenseLineDto ToLineDto(ExpenseLine l) => new()
    {
        Id = l.Id,
        ExpenseReportId = l.ExpenseReportId,
        Description = l.Description,
        Amount = l.Amount,
        AttachmentId = l.AttachmentId,
        Attachment = l.Attachment is null ? null : ToAttachmentDto(l.Attachment),
        SortOrder = l.SortOrder
    };

    internal static ExpenseAttachmentDto ToAttachmentDto(ExpenseAttachment a) => new()
    {
        Id = a.Id,
        OriginalFileName = a.OriginalFileName,
        Extension = a.Extension,
        ContentType = a.ContentType,
        SizeBytes = a.SizeBytes,
        UploadedByUserId = a.UploadedByUserId,
        UploadedAt = a.UploadedAt
    };
}
