using Humans.Application.Extensions;
using Humans.Application.Helpers;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Expenses;

/// <summary>
/// Application-layer orchestrator for Expense Reports. Coordinates
/// <see cref="IExpenseRepository"/>, audit logging, IBAN snapshots, and
/// cross-section reads via interfaces — never imports EF Core directly.
/// </summary>
public sealed class ExpenseReportService(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IBudgetService budgetService,
    ITeamService teamService,
    IUserServiceRead userService,
    IProfileService profileService,
    IAuditLogService auditLogService,
    IHoldedClient holdedClient,
    IClock clock,
    ILogger<ExpenseReportService> logger) : IExpenseReportService, IUserDataContributor
{
    public static string AttachmentKey(Guid id, string extension) =>
        $"uploads/expense-attachments/{id}{extension}";

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".heic"
        };

    public Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default)
        => repo.GetByIdAsync(id, ct);

    public async Task<ExpenseDetailViewData> GetDetailViewDataAsync(
        Guid viewerUserId, ExpenseReportDto report, CancellationToken ct = default)
    {
        var category = await budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
        var categoryName = category is not null
            ? $"{category.BudgetGroup?.Name} / {category.Name}"
            : "(unknown category)";

        var isSubmitter = report.SubmitterUserId == viewerUserId;
        var canWithdraw = report.Status is ExpenseReportStatus.Submitted
            or ExpenseReportStatus.CoordinatorEndorsed
            or ExpenseReportStatus.Approved;
        var iban = await GetSubmitterIbanViewAsync(viewerUserId, ct);

        return new ExpenseDetailViewData(
            CategoryDisplayName: categoryName,
            CanEdit: isSubmitter && report.Status == ExpenseReportStatus.Draft,
            CanSubmit: isSubmitter && report.Status == ExpenseReportStatus.Draft,
            CanWithdraw: isSubmitter && canWithdraw,
            HasIban: iban.HasIban,
            MaskedIban: iban.MaskedIban);
    }

    public Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default)
        => repo.GetForSubmitterAsync(submitterUserId, ct);

    public Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        CancellationToken ct = default)
        => repo.GetForReviewQueueAsync(ct);

    public async Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default)
    {
        var reportId = await repo.GetReportIdByAttachmentIdAsync(attachmentId, ct);
        if (reportId is null) return null;
        return await repo.GetByIdAsync(reportId.Value, ct);
    }

    public async Task<ExpenseAttachmentDownload?> TryReadAttachmentAsync(
        ExpenseReportDto owningReport,
        Guid attachmentId,
        CancellationToken ct = default)
    {
        var attachment = owningReport.Lines
            .Select(l => l.Attachment)
            .FirstOrDefault(a => a?.Id == attachmentId);
        if (attachment is null) return null;

        var bytes = await fileStorage.TryReadAsync(
            AttachmentKey(attachment.Id, attachment.Extension), ct);
        return bytes is null
            ? null
            : new ExpenseAttachmentDownload(bytes, attachment.ContentType, attachment.OriginalFileName);
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var categoryIds = await GetCoordinatorCategoryIdsAsync(coordinatorUserId, ct);
        if (categoryIds.Count == 0) return [];

        return await repo.GetByCategoryIdsAndStatusAsync(categoryIds,
            ExpenseReportStatus.Submitted, ct);
    }

    private async Task<IReadOnlyList<Guid>> GetCoordinatorCategoryIdsAsync(
        Guid coordinatorUserId, CancellationToken ct)
    {
        var teamIds = await teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinatorUserId, ct);
        if (teamIds.Count == 0) return [];

        var year = await budgetService.GetActiveYearAsync();
        if (year is null) return [];

        return year.Groups
            .SelectMany(g => g.Categories)
            .Where(c => c.TeamId.HasValue && teamIds.Contains(c.TeamId.Value))
            .Select(c => c.Id)
            .ToList();
    }

    public async Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var year = await budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var category = year.Groups.SelectMany(g => g.Categories)
            .FirstOrDefault(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException("Category not in active year.");

        var now = clock.GetCurrentInstant();
        var report = new ExpenseReport
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterUserId,
            BudgetCategoryId = category.Id,
            BudgetYearId = year.Id,
            Status = ExpenseReportStatus.Draft,
            Note = note,
            PayeeName = "",
            PayeeIban = "",
            Total = 0m,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddDraftAsync(report, ct);
        return report.Id;
    }

    public async Task UpdateDraftAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var existing = await repo.GetByIdAsync(reportId, ct)
            ?? throw new InvalidOperationException("Report not found.");
        if (existing.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can update a draft.");
        if (existing.Status != ExpenseReportStatus.Draft)
            throw new InvalidOperationException("Only Draft reports can be updated.");

        var year = await budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var category = year.Groups.SelectMany(g => g.Categories)
            .FirstOrDefault(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException("Category not in active year.");

        var updated = new ExpenseReport
        {
            Id = reportId,
            BudgetCategoryId = category.Id,
            BudgetYearId = year.Id,
            Note = note,
            UpdatedAt = clock.GetCurrentInstant()
        };
        await repo.UpdateDraftAsync(updated, ct);
    }

    public async Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateDraftAsync(reportId, submitterUserId, budgetCategoryId, note, ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    public async Task<Guid> AddLineAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var line = new ExpenseLine
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount
        };
        var ok = await repo.AddLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to add line.");
        return line.Id;
    }

    public async Task<ExpenseMutationResult> AddLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default)
    {
        try
        {
            await AddLineAsync(reportId, submitterUserId, description, amount, ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding line to report {ReportId}", reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    public async Task UpdateLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var line = new ExpenseLine
        {
            Id = lineId,
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount
        };
        var ok = await repo.UpdateLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to update line.");
    }

    public async Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default)
    {
        try
        {
            await UpdateLineAsync(reportId, submitterUserId, lineId, description, amount, ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating line {LineId} on report {ReportId}", lineId, reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    public async Task RemoveLineAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        // Clean attachment first to avoid orphan row + file blob.
        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line?.Attachment is not null)
        {
            await repo.SetLineAttachmentAsync(lineId, null, ct);
            await repo.RemoveAttachmentAsync(line.Attachment.Id, ct);
            try
            {
                await fileStorage.DeleteAsync(
                    AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not delete attachment file {AttachmentId} while removing line {LineId}",
                    line.Attachment.Id, lineId);
            }
        }

        var ok = await repo.RemoveLineAsync(reportId, lineId, ct);
        if (!ok) throw new InvalidOperationException("Failed to remove line.");
    }

    public async Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default)
    {
        try
        {
            await RemoveLineAsync(reportId, submitterUserId, lineId, ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing line {LineId} from report {ReportId}", lineId, reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    /// <summary>Max attachment size validated at the service layer (20 MB).</summary>
    private const long AttachmentMaxBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/jpg", "image/png", "image/heic"
    };

    public async Task<Guid> AttachFileToLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default)
    {
        if (content is null || content.Length == 0)
            throw new InvalidOperationException("Please select a file.");
        if (content.Length > AttachmentMaxBytes)
            throw new InvalidOperationException($"File too large. Maximum size is {AttachmentMaxBytes / (1024 * 1024)} MB.");

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!AllowedContentTypes.Contains(contentType) || !AllowedExtensions.Contains(extension))
            throw new InvalidOperationException("Unsupported file type. Upload PDF, JPEG, PNG, or HEIC.");

        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        if (!report.Lines.Any(l => l.Id == lineId))
            throw new UnauthorizedAccessException("Line does not belong to the specified report.");

        var attachmentId = Guid.NewGuid();
        await fileStorage.SaveAsync(AttachmentKey(attachmentId, extension), content, ct);

        var attachment = new ExpenseAttachment
        {
            Id = attachmentId,
            OriginalFileName = Path.GetFileName(originalFileName),
            Extension = extension,
            ContentType = contentType,
            SizeBytes = content.Length,
            UploadedByUserId = submitterUserId,
            UploadedAt = clock.GetCurrentInstant()
        };
        await repo.AddAttachmentAsync(attachment, ct);
        await repo.SetLineAttachmentAsync(lineId, attachmentId, ct);

        await auditLogService.LogAsync(
            AuditAction.ExpenseAttachmentUploaded,
            "ExpenseReport", reportId,
            $"Attachment uploaded to line {lineId}.",
            submitterUserId);

        return attachmentId;
    }

    public async Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default)
    {
        try
        {
            await AttachFileToLineAsync(reportId, submitterUserId, lineId, originalFileName, contentType, content, ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    public async Task RemoveAttachmentFromLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            throw new UnauthorizedAccessException("Line does not belong to the specified report.");

        if (line.Attachment is null) return; // idempotent

        await repo.SetLineAttachmentAsync(lineId, null, ct);
        await repo.RemoveAttachmentAsync(line.Attachment.Id, ct);

        try
        {
            await fileStorage.DeleteAsync(
                AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not delete attachment file {AttachmentId} for line {LineId}",
                line.Attachment.Id, lineId);
        }

        await auditLogService.LogAsync(
            AuditAction.ExpenseAttachmentRemoved,
            "ExpenseReport", reportId,
            $"Attachment removed from line {lineId}.",
            submitterUserId);
    }

    public async Task<bool> SubmitAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can submit.");
        if (report.Status != ExpenseReportStatus.Draft) return false;

        if (!report.Lines.Any())
            throw new InvalidOperationException("Report must have at least one line.");

        if (report.Lines.Any(l => l.AttachmentId is null))
            throw new InvalidOperationException("Every line must have an attachment before submitting.");

        var profile = (await userService.GetUserInfoAsync(submitterUserId, ct))?.Profile;
        if (profile?.Iban is null)
            throw new InvalidOperationException("Submitter must have an IBAN set on their profile.");

        // Financial records use legal name (not BurnerName). See memory/architecture/burnername-is-the-display-name.md.
        var legalName = $"{profile.FirstName} {profile.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new InvalidOperationException("Submitter must have first and last name set on their profile.");
        }
        var payeeName = legalName;
        var payeeIban = profile.Iban;

        var now = clock.GetCurrentInstant();
        var ok = await repo.SubmitAsync(reportId, payeeName, payeeIban, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseSubmit,
            "ExpenseReport", reportId,
            "Submitted expense report.",
            submitterUserId);

        return true;
    }

    public async Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        try
        {
            var submitted = await SubmitAsync(reportId, submitterUserId, ct);
            return submitted
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not submit the report. Make sure it has at least one line with an attachment and your payment IBAN is set.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error submitting expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Submission failed: {ex.Message}");
        }
    }

    public async Task<bool> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can withdraw.");

        var now = clock.GetCurrentInstant();
        var ok = await repo.WithdrawAsync(reportId, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseWithdraw,
            "ExpenseReport", reportId,
            "Withdrew expense report.",
            submitterUserId);

        return true;
    }
    public async Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        try
        {
            var withdrawn = await WithdrawAsync(reportId, submitterUserId, ct);
            return withdrawn
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not withdraw this report.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error withdrawing expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Withdrawal failed: {ex.Message}");
        }
    }

    public async Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid submitterUserId, string? iban, CancellationToken ct = default)
    {
        var ibanValue = string.IsNullOrWhiteSpace(iban) ? null : iban.Trim();
        var existingIban = (await userService.GetUserInfoAsync(submitterUserId, ct))?.Profile?.Iban;

        if (ibanValue is not null && !IbanValidator.IsValid(ibanValue))
            return IbanFailure("Invalid IBAN format.", isValidationError: true, existingIban);

        var normalized = ibanValue is null ? null : IbanValidator.Normalize(ibanValue);
        try
        {
            var saved = await profileService.SetIbanAsync(submitterUserId, normalized, ct);
            if (!saved)
                return IbanFailure("Failed to save IBAN.", isValidationError: false, existingIban);

            return new ExpenseIbanSaveResult(
                Succeeded: true,
                IsValidationError: false,
                Message: normalized is null ? "IBAN removed." : "IBAN saved.",
                HasIban: normalized is not null,
                MaskedIban: normalized is null ? null : IbanFormatter.Mask(normalized));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting IBAN for user {UserId}", submitterUserId);
            return IbanFailure("Failed to save IBAN.", isValidationError: false, existingIban);
        }
    }

    public async Task<ExpenseIbanViewData> GetSubmitterIbanViewAsync(
        Guid submitterUserId, CancellationToken ct = default)
    {
        var iban = (await userService.GetUserInfoAsync(submitterUserId, ct))?.Profile?.Iban;
        var hasIban = !string.IsNullOrEmpty(iban);
        return new ExpenseIbanViewData(
            HasIban: hasIban,
            MaskedIban: hasIban ? IbanFormatter.Mask(iban!) : null);
    }

    private static ExpenseIbanSaveResult IbanFailure(string message, bool isValidationError, string? existingIban)
    {
        var hasIban = !string.IsNullOrEmpty(existingIban);
        return new ExpenseIbanSaveResult(
            Succeeded: false,
            IsValidationError: isValidationError,
            Message: message,
            HasIban: hasIban,
            MaskedIban: hasIban ? IbanFormatter.Mask(existingIban!) : null);
    }

    public async Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = clock.GetCurrentInstant();
        var ok = await repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseEndorse,
            "ExpenseReport", reportId,
            "Coordinator endorsed expense report.",
            coordinatorUserId);

        return true;
    }

    public async Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default)
    {
        try
        {
            var endorsed = await CoordinatorEndorseAsync(reportId, coordinatorUserId, ct);
            return endorsed
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not endorse the report. It may no longer be in Submitted status.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error endorsing expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Endorsement failed: {ex.Message}");
        }
    }

    public async Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = clock.GetCurrentInstant();
        var ok = await repo.CoordinatorRejectAsync(reportId, coordinatorUserId, reason, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseCoordinatorReject,
            "ExpenseReport", reportId,
            $"Coordinator rejected expense report: {reason}",
            coordinatorUserId);

        return true;
    }

    public async Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default)
    {
        try
        {
            var rejected = await CoordinatorRejectAsync(reportId, coordinatorUserId, reason, ct);
            return rejected
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not reject the report. It may no longer be in Submitted status.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error coordinator-rejecting expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Rejection failed: {ex.Message}");
        }
    }

    public async Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var outboxEventId = Guid.NewGuid();
        var now = clock.GetCurrentInstant();
        var ok = await repo.ApproveAsync(reportId, actorUserId, overrideCategoryId, now, outboxEventId, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseApprove,
            "ExpenseReport", reportId,
            "Finance approved expense report.",
            actorUserId);

        if (overrideCategoryId.HasValue && overrideCategoryId.Value != report.BudgetCategoryId)
        {
            await auditLogService.LogAsync(
                AuditAction.ExpenseCategoryOverride,
                "ExpenseReport", reportId,
                $"Category overridden during approval to {overrideCategoryId.Value}.",
                actorUserId);
        }

        return true;
    }

    public async Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default)
    {
        try
        {
            var approved = await ApproveAsync(reportId, actorUserId, overrideCategoryId, ct);
            return approved
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not approve the report. It may not be in an approvable status.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error approving expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Approval failed: {ex.Message}");
        }
    }

    public async Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var now = clock.GetCurrentInstant();
        var ok = await repo.FinanceRejectAsync(reportId, actorUserId, reason, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseReject,
            "ExpenseReport", reportId,
            $"Finance rejected expense report: {reason}",
            actorUserId);

        return true;
    }

    public async Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default)
    {
        try
        {
            var rejected = await FinanceRejectAsync(reportId, actorUserId, reason, ct);
            return rejected
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not reject the report. It may not be in a rejectable status.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error finance-rejecting expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Rejection failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<Guid>> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds, Guid actorUserId,
        CancellationToken ct = default)
    {
        if (reportIds.Count == 0) return [];

        var now = clock.GetCurrentInstant();
        var flippedIds = await repo.MarkSepaSentAsync(reportIds, now, ct);

        // Audit only reports that flipped; repo skips ineligible (e.g. status != Approved).
        foreach (var id in flippedIds)
        {
            await auditLogService.LogAsync(
                AuditAction.ExpenseSepaSent,
                "ExpenseReport", id,
                "Marked as SEPA sent.",
                actorUserId);
        }

        return flippedIds;
    }

    public async Task<bool> MarkPaidAsync(
        Guid reportId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var ok = await repo.MarkPaidAsync(reportId, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpensePaid,
            "ExpenseReport", reportId,
            "Marked as paid.",
            "ExpensePaidJob");

        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
        Guid categoryId, CancellationToken ct = default)
    {
        // True iff category's team has ≥1 active Coordinator (cache hit, no DB).
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null || category.TeamId is null)
            return false;

        var team = await teamService.GetTeamAsync(category.TeamId.Value, ct);
        if (team is null)
            return false;

        return team.Members.Any(m => m.Role == TeamMemberRole.Coordinator);
    }

    /// <inheritdoc/>
    public async Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default)
    {
        var events = await repo
            .GetUnprocessedOutboxAsync(batchSize, ct);

        if (events.Count == 0)
        {
            return;
        }

        foreach (var outboxEvent in events)
        {
            try
            {
                var report = await repo
                    .GetByIdAsync(outboxEvent.ExpenseReportId, ct);

                if (report is null)
                {
                    logger.LogWarning(
                        "Outbox event {OutboxEventId} references missing report {ReportId} — marking permanently failed",
                        outboxEvent.Id, outboxEvent.ExpenseReportId);
                    await repo.MarkOutboxFailedPermanentlyAsync(
                        outboxEvent.Id,
                        "Report not found",
                        clock.GetCurrentInstant(),
                        ct);
                    continue;
                }

                var category = await budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
                var tag = BuildHoldedTag(category?.BudgetGroup?.Name, category?.Name);

                var submitterName = string.IsNullOrWhiteSpace(report.PayeeName)
                    ? "Unknown"
                    : report.PayeeName;

                var now = clock.GetCurrentInstant();

                switch (outboxEvent.EventType)
                {
                    case HoldedExpenseOutboxEventType.CreateIncomingDoc:
                        await ProcessHoldedCreateAsync(
                            outboxEvent.Id, report, tag, submitterName, now, ct);
                        break;

                    case HoldedExpenseOutboxEventType.UpdateIncomingDocTag:
                        await ProcessHoldedUpdateTagAsync(
                            outboxEvent.Id, report, tag, now, ct);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown outbox event type '{outboxEvent.EventType}'.");
                }
            }
            catch (HoldedTransientException ex)
            {
                logger.LogWarning(
                    ex,
                    "Transient error processing Holded outbox event {OutboxEventId} — will retry",
                    outboxEvent.Id);
                await repo.IncrementOutboxRetryAsync(
                    outboxEvent.Id, ex.Message, ct);
            }
            catch (HoldedPermanentException ex)
            {
                logger.LogError(
                    ex,
                    "Permanent error processing Holded outbox event {OutboxEventId} — HTTP {StatusCode}",
                    outboxEvent.Id, ex.StatusCode);
                await repo.MarkOutboxFailedPermanentlyAsync(
                    outboxEvent.Id, ex.Message, clock.GetCurrentInstant(), ct);
            }
        }
    }

    /// <inheritdoc/>
    public async Task PollHoldedPaidStatusAsync(int batchSize, CancellationToken ct = default)
    {
        var reports = await repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, ct);

        var batch = reports
            .OrderBy(r => r.SepaSentAt ?? r.CreatedAt)
            .Take(batchSize)
            .ToList();

        if (batch.Count == 0)
            return;

        foreach (var report in batch)
        {
            if (report.HoldedDocId is null)
            {
                logger.LogWarning(
                    "SepaSent report {ReportId} has no HoldedDocId — skipping",
                    report.Id);
                continue;
            }

            try
            {
                var doc = await holdedClient.GetPurchaseDocumentAsync(report.HoldedDocId, ct);

                if (doc.PaymentsPending == 0 && doc.ApprovedAt is not null)
                {
                    await MarkPaidAsync(report.Id, ct);
                    logger.LogInformation(
                        "Marked expense report {ReportId} as Paid (HoldedDocId={HoldedDocId})",
                        report.Id, report.HoldedDocId);
                }
            }
            catch (HoldedPermanentException ex) when (ex.StatusCode == 404)
            {
                logger.LogWarning(
                    "Holded doc {HoldedDocId} for report {ReportId} deleted out-of-band — skipping",
                    report.HoldedDocId, report.Id);
            }
            catch (HoldedTransientException ex)
            {
                logger.LogWarning(
                    ex,
                    "Transient error polling Holded for report {ReportId} (HoldedDocId={HoldedDocId}) — will retry next run",
                    report.Id, report.HoldedDocId);
            }
        }
    }

    private async Task ProcessHoldedCreateAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string tag,
        string submitterName,
        Instant now,
        CancellationToken ct)
    {
        var input = new HoldedPurchaseDocumentInput
        {
            ContactName = submitterName,
            Date = report.SubmittedAt ?? report.CreatedAt,
            Description = report.Note ?? "",
            Tags = [tag],
            Lines = report.Lines
                .OrderBy(l => l.SortOrder)
                .Select(l => new HoldedPurchaseDocumentLineInput
                {
                    Description = l.Description,
                    Amount = l.Amount,
                    Tags = [tag],
                })
                .ToList(),
        };

        // Idempotency: reuse existing Holded doc id if a prior retry failed during attachment upload.
        string holdedDocId;
        if (string.IsNullOrEmpty(report.HoldedDocId))
        {
            holdedDocId = await holdedClient.CreatePurchaseDocumentAsync(input, ct);
            await repo.SetHoldedDocIdAsync(report.Id, holdedDocId, now, ct);
        }
        else
        {
            holdedDocId = report.HoldedDocId;
        }

        foreach (var line in report.Lines.OrderBy(l => l.SortOrder))
        {
            if (line.AttachmentId is null || line.Attachment is null)
            {
                continue;
            }

            var bytes = await fileStorage.TryReadAsync(
                AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
            if (bytes is null)
            {
                // Throw so the outbox event stays unprocessed and Hangfire retries.
                throw new InvalidOperationException(
                    $"Attachment file for {line.Attachment.Id}{line.Attachment.Extension} could not be read from storage.");
            }
            using var stream = new MemoryStream(bytes, writable: false);
            await holdedClient.UploadAttachmentAsync(
                holdedDocId,
                new HoldedAttachmentInput
                {
                    FileName = line.Attachment.OriginalFileName,
                    ContentType = line.Attachment.ContentType,
                    Content = stream,
                },
                ct);
        }

        await repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);
    }

    private async Task ProcessHoldedUpdateTagAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string tag,
        Instant now,
        CancellationToken ct)
    {
        await holdedClient.UpdatePurchaseDocumentTagsAsync(
            report.HoldedDocId!,
            [tag],
            ct);

        await repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);
    }

    private static string BuildHoldedTag(string? groupName, string? categoryName)
    {
        var groupSlug = string.IsNullOrWhiteSpace(groupName)
            ? "unknown"
            : SlugHelper.GenerateSlug(groupName);
        var categorySlug = string.IsNullOrWhiteSpace(categoryName)
            ? "unknown"
            : SlugHelper.GenerateSlug(categoryName);
        return $"{groupSlug}-{categorySlug}";
    }

    /// <summary>Loads report; enforces submitter + editable state (Draft/Submitted/CoordinatorEndorsed).</summary>
    private async Task<ExpenseReportDto> RequireEditableReportAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct)
    {
        var report = await repo.GetByIdAsync(reportId, ct)
            ?? throw new InvalidOperationException("Report not found.");
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can edit lines.");
        // Line mutations are Draft-only — submitted reports are frozen for review.
        if (report.Status is not ExpenseReportStatus.Draft)
            throw new InvalidOperationException(
                $"Lines cannot be edited when the report is in status {report.Status}.");
        return report;
    }

    /// <summary>
    /// Checks the actor is a coordinator of the team that owns the category.
    /// Throws <see cref="UnauthorizedAccessException"/> if not.
    /// </summary>
    private async Task RequireCoordinatorForCategoryAsync(
        Guid categoryId, Guid actorUserId, CancellationToken ct)
    {
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null)
            throw new InvalidOperationException("Budget category not found.");
        if (!category.TeamId.HasValue)
            throw new UnauthorizedAccessException(
                "Category has no owning team; coordinator endorsement is not valid.");
        var isCoordinator = await teamService.IsUserCoordinatorOfTeamAsync(
            category.TeamId.Value, actorUserId, ct);
        if (!isCoordinator)
            throw new UnauthorizedAccessException("Actor is not a coordinator of the category's team.");
    }

    /// <summary>User's reports (lines+attachment metadata), masked IBAN, audit. Chain-follows merge tombstones.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);

        var allIds = new List<Guid>(sourceIds.Count + 1);
        allIds.AddRange(sourceIds);
        allIds.Add(userId);

        var allReports = new List<ExpenseReportDto>();
        foreach (var id in allIds)
        {
            var reports = await repo.GetForSubmitterAsync(id, ct);
            allReports.AddRange(reports);
        }

        var profile = (await userService.GetUserInfoAsync(userId, ct))?.Profile;
        var maskedIban = string.IsNullOrEmpty(profile?.Iban)
            ? null
            : IbanFormatter.Mask(profile.Iban);

        var expenseActions = new List<AuditAction>
        {
            AuditAction.ExpenseSubmit,
            AuditAction.ExpenseEndorse,
            AuditAction.ExpenseCoordinatorReject,
            AuditAction.ExpenseApprove,
            AuditAction.ExpenseReject,
            AuditAction.ExpenseWithdraw,
            AuditAction.ExpenseCategoryOverride,
            AuditAction.ExpenseSepaSent,
            AuditAction.ExpensePaid,
            AuditAction.ExpenseAttachmentUploaded,
            AuditAction.ExpenseAttachmentRemoved,
            AuditAction.IbanSet,
            AuditAction.IbanRemove,
            AuditAction.IbanReveal,
        };

        var auditEntries = await auditLogService.GetFilteredEntriesAsync(
            userId: userId,
            actions: expenseActions,
            limit: 10_000,
            ct: ct);

        var shapedReports = allReports
            .OrderBy(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.Note,
                r.PayeeName,
                PayeeIban = IbanFormatter.Mask(r.PayeeIban),
                r.Total,
                r.SubmittedAt,
                r.ApprovedAt,
                r.SepaSentAt,
                r.PaidAt,
                r.CreatedAt,
                Lines = r.Lines.Select(l => new
                {
                    l.Id,
                    l.Description,
                    l.Amount,
                    l.SortOrder,
                    Attachment = l.Attachment is null
                        ? null
                        : new
                        {
                            l.Attachment.OriginalFileName,
                            l.Attachment.ContentType,
                            l.Attachment.SizeBytes,
                        }
                }).ToList()
            }).ToList();

        var shapedAudit = auditEntries
            .Select(e => new
            {
                e.Action,
                e.EntityType,
                e.EntityId,
                e.Description,
                OccurredAt = e.OccurredAt.ToInvariantInstantString()
            }).ToList();

        return
        [
            new UserDataSlice(GdprExportSections.ExpenseReports,
                shapedReports.Count > 0 ? shapedReports : null),
            new UserDataSlice(GdprExportSections.ExpenseAuditLog,
                shapedAudit.Count > 0
                    ? new { MaskedIban = maskedIban, Entries = shapedAudit }
                    : (object?)null),
        ];
    }
}
