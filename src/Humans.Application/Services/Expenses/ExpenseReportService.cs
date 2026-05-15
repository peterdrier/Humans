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
public sealed class ExpenseReportService : IExpenseReportService, IUserDataContributor
{
    private readonly IExpenseRepository _repo;
    private readonly IFileStorage _fileStorage;
    private readonly IBudgetService _budgetService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly IAuditLogService _auditLogService;
    private readonly IHoldedClient _holdedClient;
    private readonly IClock _clock;
    private readonly ILogger<ExpenseReportService> _logger;

    public ExpenseReportService(
        IExpenseRepository repo,
        IFileStorage fileStorage,
        IBudgetService budgetService,
        ITeamService teamService,
        IUserService userService,
        IProfileService profileService,
        IAuditLogService auditLogService,
        IHoldedClient holdedClient,
        IClock clock,
        ILogger<ExpenseReportService> logger)
    {
        _repo = repo;
        _fileStorage = fileStorage;
        _budgetService = budgetService;
        _teamService = teamService;
        _userService = userService;
        _profileService = profileService;
        _auditLogService = auditLogService;
        _holdedClient = holdedClient;
        _clock = clock;
        _logger = logger;
    }

    public static string AttachmentKey(Guid id, string extension) =>
        $"uploads/expense-attachments/{id}{extension}";

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".heic"
        };

    // ─────────────────────────────── Reads ───────────────────────────────────

    public Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default)
        => _repo.GetByIdAsync(id, ct);

    public async Task<ExpenseDetailViewData> GetDetailViewDataAsync(
        Guid viewerUserId, ExpenseReportDto report, CancellationToken ct = default)
    {
        var category = await _budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
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
        => _repo.GetForSubmitterAsync(submitterUserId, ct);

    public Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        CancellationToken ct = default)
        => _repo.GetForReviewQueueAsync(ct);

    public async Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default)
    {
        var reportId = await _repo.GetReportIdByAttachmentIdAsync(attachmentId, ct);
        if (reportId is null) return null;
        return await _repo.GetByIdAsync(reportId.Value, ct);
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

        var bytes = await _fileStorage.TryReadAsync(
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

        return await _repo.GetByCategoryIdsAndStatusAsync(categoryIds,
            ExpenseReportStatus.Submitted, ct);
    }

    private async Task<IReadOnlyList<Guid>> GetCoordinatorCategoryIdsAsync(
        Guid coordinatorUserId, CancellationToken ct)
    {
        var teamIds = await _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinatorUserId, ct);
        if (teamIds.Count == 0) return [];

        var year = await _budgetService.GetActiveYearAsync();
        if (year is null) return [];

        return year.Groups
            .SelectMany(g => g.Categories)
            .Where(c => c.TeamId.HasValue && teamIds.Contains(c.TeamId.Value))
            .Select(c => c.Id)
            .ToList();
    }

    // ──────────────────────────── Draft CRUD ─────────────────────────────────

    public async Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var year = await _budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var category = year.Groups.SelectMany(g => g.Categories)
            .FirstOrDefault(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException("Category not in active year.");

        var now = _clock.GetCurrentInstant();
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
        await _repo.AddDraftAsync(report, ct);
        return report.Id;
    }

    public async Task UpdateDraftAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var existing = await _repo.GetByIdAsync(reportId, ct)
            ?? throw new InvalidOperationException("Report not found.");
        if (existing.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can update a draft.");
        if (existing.Status != ExpenseReportStatus.Draft)
            throw new InvalidOperationException("Only Draft reports can be updated.");

        var year = await _budgetService.GetActiveYearAsync()
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
            UpdatedAt = _clock.GetCurrentInstant()
        };
        await _repo.UpdateDraftAsync(updated, ct);
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
            _logger.LogError(ex, "Error updating expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    // ────────────────────────── Line Methods ─────────────────────────────────

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
        var ok = await _repo.AddLineAsync(reportId, line, ct);
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
            _logger.LogError(ex, "Error adding line to report {ReportId}", reportId);
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
        var ok = await _repo.UpdateLineAsync(reportId, line, ct);
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
            _logger.LogError(ex, "Error updating line {LineId} on report {ReportId}", lineId, reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    public async Task RemoveLineAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        // If the line has an attachment, clean it up first so we don't leak an
        // orphan attachment row + file blob when the line goes away.
        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line?.Attachment is not null)
        {
            await _repo.SetLineAttachmentAsync(lineId, null, ct);
            await _repo.RemoveAttachmentAsync(line.Attachment.Id, ct);
            try
            {
                await _fileStorage.DeleteAsync(
                    AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not delete attachment file {AttachmentId} while removing line {LineId}",
                    line.Attachment.Id, lineId);
            }
        }

        var ok = await _repo.RemoveLineAsync(reportId, lineId, ct);
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
            _logger.LogError(ex, "Error removing line {LineId} from report {ReportId}", lineId, reportId);
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
        await _fileStorage.SaveAsync(AttachmentKey(attachmentId, extension), content, ct);

        var attachment = new ExpenseAttachment
        {
            Id = attachmentId,
            OriginalFileName = Path.GetFileName(originalFileName),
            Extension = extension,
            ContentType = contentType,
            SizeBytes = content.Length,
            UploadedByUserId = submitterUserId,
            UploadedAt = _clock.GetCurrentInstant()
        };
        await _repo.AddAttachmentAsync(attachment, ct);
        await _repo.SetLineAttachmentAsync(lineId, attachmentId, ct);

        await _auditLogService.LogAsync(
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
            _logger.LogError(ex, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);
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

        // Unlink first, then delete the row, then delete the file.
        await _repo.SetLineAttachmentAsync(lineId, null, ct);
        await _repo.RemoveAttachmentAsync(line.Attachment.Id, ct);

        try
        {
            await _fileStorage.DeleteAsync(
                AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not delete attachment file {AttachmentId} for line {LineId}",
                line.Attachment.Id, lineId);
        }

        await _auditLogService.LogAsync(
            AuditAction.ExpenseAttachmentRemoved,
            "ExpenseReport", reportId,
            $"Attachment removed from line {lineId}.",
            submitterUserId);
    }

    // ───────────────────────── Submit / Withdraw ──────────────────────────────

    public async Task<bool> SubmitAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can submit.");
        if (report.Status != ExpenseReportStatus.Draft) return false;

        // Validate: at least 1 line
        if (!report.Lines.Any())
            throw new InvalidOperationException("Report must have at least one line.");

        // Validate: every line has an attachment
        if (report.Lines.Any(l => l.AttachmentId is null))
            throw new InvalidOperationException("Every line must have an attachment before submitting.");

        // Validate + snapshot IBAN
        var profile = (await _userService.GetUserInfoAsync(submitterUserId, ct))?.Profile;
        if (profile?.Iban is null)
            throw new InvalidOperationException("Submitter must have an IBAN set on their profile.");

        // SEPA pain.001 and Holded purchase docs are financial records — the payee name must be
        // the legal identity that matches the bank-account holder, not the community pseudonym
        // (BurnerName). Profile.FirstName + LastName carry the legal name; fall back to
        // User.DisplayName for stub profiles where the legal-name fields were never populated.
        // This is the documented carve-out from memory/architecture/burnername-is-the-display-name.md.
        var legalName = $"{profile.FirstName} {profile.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(legalName))
        {
            var user = await _userService.GetByIdAsync(submitterUserId, ct);
            legalName = user?.DisplayName ?? "";
        }
        var payeeName = legalName;
        var payeeIban = profile.Iban;

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.SubmitAsync(reportId, payeeName, payeeIban, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseSubmit,
            "ExpenseReport", reportId,
            $"Submitted expense report.",
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
            _logger.LogError(ex, "Error submitting expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Submission failed: {ex.Message}");
        }
    }

    public async Task<bool> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can withdraw.");

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.WithdrawAsync(reportId, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseWithdraw,
            "ExpenseReport", reportId,
            $"Withdrew expense report.",
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
            _logger.LogError(ex, "Error withdrawing expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Withdrawal failed: {ex.Message}");
        }
    }

    // ─────────────────────── Coordinator Endorsement ─────────────────────────

    public async Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid submitterUserId, string? iban, CancellationToken ct = default)
    {
        var ibanValue = string.IsNullOrWhiteSpace(iban) ? null : iban.Trim();
        var profile = await _profileService.GetProfileAsync(submitterUserId, ct);

        if (ibanValue is not null && !IbanValidator.IsValid(ibanValue))
            return IbanFailure("Invalid IBAN format.", isValidationError: true, profile);

        var normalized = ibanValue is null ? null : IbanValidator.Normalize(ibanValue);
        try
        {
            var saved = await _profileService.SetIbanAsync(submitterUserId, normalized, ct);
            if (!saved)
                return IbanFailure("Failed to save IBAN.", isValidationError: false, profile);

            return new ExpenseIbanSaveResult(
                Succeeded: true,
                IsValidationError: false,
                Message: normalized is null ? "IBAN removed." : "IBAN saved.",
                HasIban: normalized is not null,
                MaskedIban: normalized is null ? null : IbanFormatter.Mask(normalized));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting IBAN for user {UserId}", submitterUserId);
            return IbanFailure("Failed to save IBAN.", isValidationError: false, profile);
        }
    }

    public async Task<ExpenseIbanViewData> GetSubmitterIbanViewAsync(
        Guid submitterUserId, CancellationToken ct = default)
    {
        var profile = await _profileService.GetProfileAsync(submitterUserId, ct);
        var hasIban = !string.IsNullOrEmpty(profile?.Iban);
        return new ExpenseIbanViewData(
            HasIban: hasIban,
            MaskedIban: hasIban ? IbanFormatter.Mask(profile!.Iban!) : null);
    }

    private static ExpenseIbanSaveResult IbanFailure(string message, bool isValidationError, Domain.Entities.Profile? profile)
    {
        var hasIban = !string.IsNullOrEmpty(profile?.Iban);
        return new ExpenseIbanSaveResult(
            Succeeded: false,
            IsValidationError: isValidationError,
            Message: message,
            HasIban: hasIban,
            MaskedIban: hasIban ? IbanFormatter.Mask(profile!.Iban!) : null);
    }

    public async Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseEndorse,
            "ExpenseReport", reportId,
            $"Coordinator endorsed expense report.",
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
            _logger.LogError(ex, "Error endorsing expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Endorsement failed: {ex.Message}");
        }
    }

    public async Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.CoordinatorRejectAsync(reportId, coordinatorUserId, reason, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
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
            _logger.LogError(ex, "Error coordinator-rejecting expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Rejection failed: {ex.Message}");
        }
    }

    // ────────────────────── Finance Approve / Reject ──────────────────────────

    public async Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var outboxEventId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var ok = await _repo.ApproveAsync(reportId, actorUserId, overrideCategoryId, now, outboxEventId, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseApprove,
            "ExpenseReport", reportId,
            $"Finance approved expense report.",
            actorUserId);

        if (overrideCategoryId.HasValue && overrideCategoryId.Value != report.BudgetCategoryId)
        {
            await _auditLogService.LogAsync(
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
            _logger.LogError(ex, "Error approving expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Approval failed: {ex.Message}");
        }
    }

    public async Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await _repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var now = _clock.GetCurrentInstant();
        var ok = await _repo.FinanceRejectAsync(reportId, actorUserId, reason, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpenseReject,
            "ExpenseReport", reportId,
            $"Finance rejected expense report: {reason}",
            actorUserId);

        return true;
    }

    // ─────────────────────────── SEPA + Paid ─────────────────────────────────

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
            _logger.LogError(ex, "Error finance-rejecting expense report {ReportId}", reportId);
            return ExpenseMutationResult.Failure($"Rejection failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<Guid>> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds, Guid actorUserId,
        CancellationToken ct = default)
    {
        if (reportIds.Count == 0) return [];

        var now = _clock.GetCurrentInstant();
        var flippedIds = await _repo.MarkSepaSentAsync(reportIds, now, ct);

        // Audit one entry per report that actually flipped — never for ids the
        // repo skipped (e.g. status != Approved).
        foreach (var id in flippedIds)
        {
            await _auditLogService.LogAsync(
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
        var now = _clock.GetCurrentInstant();
        var ok = await _repo.MarkPaidAsync(reportId, now, ct);
        if (!ok) return false;

        await _auditLogService.LogAsync(
            AuditAction.ExpensePaid,
            "ExpenseReport", reportId,
            "Marked as paid.",
            "ExpensePaidJob");

        return true;
    }

    // ─────────────────────── Coordinator Detection ───────────────────────────

    /// <inheritdoc/>
    public Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
        Guid categoryId, CancellationToken ct = default)
    {
        // TODO: Wire up real coordinator detection once ITeamService exposes TeamInfo with Coordinators.
        // For now returns false — submitter→FinanceAdmin path is the only active workflow.
        return Task.FromResult(false);
    }

    // ─────────────────────────── Holded Outbox ───────────────────────────────

    /// <inheritdoc/>
    public async Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default)
    {
        var events = await _repo
            .GetUnprocessedOutboxAsync(batchSize, ct);

        if (events.Count == 0)
        {
            return;
        }

        foreach (var outboxEvent in events)
        {
            try
            {
                var report = await _repo
                    .GetByIdAsync(outboxEvent.ExpenseReportId, ct);

                if (report is null)
                {
                    _logger.LogWarning(
                        "Outbox event {OutboxEventId} references missing report {ReportId} — marking permanently failed",
                        outboxEvent.Id, outboxEvent.ExpenseReportId);
                    await _repo.MarkOutboxFailedPermanentlyAsync(
                        outboxEvent.Id,
                        "Report not found",
                        _clock.GetCurrentInstant(),
                        ct);
                    continue;
                }

                var category = await _budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
                var tag = BuildHoldedTag(category?.BudgetGroup?.Name, category?.Name);

                var users = await _userService.GetUserInfosAsync(
                    [report.SubmitterUserId], ct);
                var submitterName = users.TryGetValue(report.SubmitterUserId, out var user)
                    ? user.DisplayName
                    : "Unknown";

                var now = _clock.GetCurrentInstant();

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
                _logger.LogWarning(
                    ex,
                    "Transient error processing Holded outbox event {OutboxEventId} — will retry",
                    outboxEvent.Id);
                await _repo.IncrementOutboxRetryAsync(
                    outboxEvent.Id, ex.Message, ct);
            }
            catch (HoldedPermanentException ex)
            {
                _logger.LogError(
                    ex,
                    "Permanent error processing Holded outbox event {OutboxEventId} — HTTP {StatusCode}",
                    outboxEvent.Id, ex.StatusCode);
                await _repo.MarkOutboxFailedPermanentlyAsync(
                    outboxEvent.Id, ex.Message, _clock.GetCurrentInstant(), ct);
            }
        }
    }

    /// <inheritdoc/>
    public async Task PollHoldedPaidStatusAsync(int batchSize, CancellationToken ct = default)
    {
        var reports = await _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, ct);

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
                _logger.LogWarning(
                    "SepaSent report {ReportId} has no HoldedDocId — skipping",
                    report.Id);
                continue;
            }

            try
            {
                var doc = await _holdedClient.GetPurchaseDocumentAsync(report.HoldedDocId, ct);

                if (doc.PaymentsPending == 0 && doc.ApprovedAt is not null)
                {
                    await this.MarkPaidAsync(report.Id, ct);
                    _logger.LogInformation(
                        "Marked expense report {ReportId} as Paid (HoldedDocId={HoldedDocId})",
                        report.Id, report.HoldedDocId);
                }
            }
            catch (HoldedPermanentException ex) when (ex.StatusCode == 404)
            {
                _logger.LogWarning(
                    "Holded doc {HoldedDocId} for report {ReportId} deleted out-of-band — skipping",
                    report.HoldedDocId, report.Id);
            }
            catch (HoldedTransientException ex)
            {
                _logger.LogWarning(
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

        // Idempotency: if a prior retry already issued the Holded document but failed
        // during attachment upload, reuse the existing id instead of creating a duplicate.
        // SetHoldedDocIdAsync runs IMMEDIATELY after the create call so a transient
        // upload failure cannot cause a retry to call CreatePurchaseDocumentAsync again.
        string holdedDocId;
        if (string.IsNullOrEmpty(report.HoldedDocId))
        {
            holdedDocId = await _holdedClient.CreatePurchaseDocumentAsync(input, ct);
            await _repo.SetHoldedDocIdAsync(report.Id, holdedDocId, now, ct);
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

            var bytes = await _fileStorage.TryReadAsync(
                AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
            if (bytes is null)
            {
                // TryReadAsync returns null for both missing files and IO errors; either
                // way, swallowing it would mark the outbox event processed without ever
                // uploading the receipt to Holded. Throw so the outer handler leaves the
                // event unprocessed and Hangfire retries the job.
                throw new InvalidOperationException(
                    $"Attachment file for {line.Attachment.Id}{line.Attachment.Extension} could not be read from storage.");
            }
            using var stream = new MemoryStream(bytes, writable: false);
            await _holdedClient.UploadAttachmentAsync(
                holdedDocId,
                new HoldedAttachmentInput
                {
                    FileName = line.Attachment.OriginalFileName,
                    ContentType = line.Attachment.ContentType,
                    Content = stream,
                },
                ct);
        }

        await _repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);
    }

    private async Task ProcessHoldedUpdateTagAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string tag,
        Instant now,
        CancellationToken ct)
    {
        await _holdedClient.UpdatePurchaseDocumentTagsAsync(
            report.HoldedDocId!,
            [tag],
            ct);

        await _repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);
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

    // ─────────────────────────── Private Helpers ─────────────────────────────

    /// <summary>
    /// Loads the report (with lines) and enforces that: the caller is the submitter, and
    /// the report is in a state that allows line/attachment edits
    /// ({Draft, Submitted, CoordinatorEndorsed}).
    /// </summary>
    private async Task<ExpenseReportDto> RequireEditableReportAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct)
    {
        var report = await _repo.GetByIdAsync(reportId, ct)
            ?? throw new InvalidOperationException("Report not found.");
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can edit lines.");
        // Line mutations are Draft-only — once submitted, the coordinator and Finance
        // review a frozen set of lines + attachments. Post-submission edits would let
        // the submitter alter a report mid-review (after endorsement, even).
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
        var category = await _budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null)
            throw new InvalidOperationException("Budget category not found.");
        if (!category.TeamId.HasValue)
            throw new UnauthorizedAccessException(
                "Category has no owning team; coordinator endorsement is not valid.");
        var isCoordinator = await _teamService.IsUserCoordinatorOfTeamAsync(
            category.TeamId.Value, actorUserId, ct);
        if (!isCoordinator)
            throw new UnauthorizedAccessException("Actor is not a coordinator of the category's team.");
    }

    // ─────────────────────── IUserDataContributor (GDPR) ─────────────────────

    /// <summary>
    /// Returns the user's expense reports (with lines and attachment metadata —
    /// no bytes), a masked IBAN snapshot, and expense-related audit-log entries.
    /// Chain-follows merge tombstones so a fold-target's export includes reports
    /// submitted under merged source ids.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var sourceIds = await _userService.GetMergedSourceIdsAsync(userId, ct);

        // Collect all ids whose reports we should include
        var allIds = new List<Guid>(sourceIds.Count + 1);
        allIds.AddRange(sourceIds);
        allIds.Add(userId);

        // GetForSubmitterAsync already returns fully-populated DTOs (lines + attachments).
        var allReports = new List<ExpenseReportDto>();
        foreach (var id in allIds)
        {
            var reports = await _repo.GetForSubmitterAsync(id, ct);
            allReports.AddRange(reports);
        }

        // Fetch current IBAN from profile (masked per spec)
        var profile = (await _userService.GetUserInfoAsync(userId, ct))?.Profile;
        var maskedIban = string.IsNullOrEmpty(profile?.Iban)
            ? null
            : IbanFormatter.Mask(profile.Iban);

        // Expense-related audit actions (this user as actor or subject)
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

        var auditEntries = await _auditLogService.GetFilteredEntriesAsync(
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
