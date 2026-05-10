using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Expenses")]
public sealed class ExpensesController : HumansControllerBase
{
    private readonly IExpenseReportService _service;
    private readonly IExpenseAttachmentStorageService _storage;
    private readonly IBudgetService _budgetService;
    private readonly IProfileService _profileService;
    private readonly IExpenseRepository _repo;
    private readonly IClock _clock;
    private readonly ILogger<ExpensesController> _logger;

    /// <summary>Max attachment size enforced before calling the storage service (20 MB).</summary>
    private const long AttachmentMaxBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/heic"
    };

    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/heic"] = ".heic"
    };

    public ExpensesController(
        UserManager<User> userManager,
        IExpenseReportService service,
        IExpenseAttachmentStorageService storage,
        IBudgetService budgetService,
        IProfileService profileService,
        IExpenseRepository repo,
        IClock clock,
        ILogger<ExpensesController> logger)
        : base(userManager)
    {
        _service = service;
        _storage = storage;
        _budgetService = budgetService;
        _profileService = profileService;
        _repo = repo;
        _clock = clock;
        _logger = logger;
    }

    // ───────────────────────────── 6.1  Index ────────────────────────────────

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var reports = await _service.GetForSubmitterAsync(user.Id);
            var activeYear = await _budgetService.GetActiveYearAsync();
            var profile = await _profileService.GetProfileAsync(user.Id);

            var model = new ExpensesIndexViewModel
            {
                Reports = reports,
                HasActiveYear = activeYear is not null,
                HasIban = !string.IsNullOrEmpty(profile?.Iban)
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading expense reports index for user");
            SetError("Failed to load expense reports.");
            return View(new ExpensesIndexViewModel
            {
                Reports = [],
                HasActiveYear = false,
                HasIban = false
            });
        }
    }

    // ───────────────────────────── 6.2  New ──────────────────────────────────

    [HttpGet("New")]
    public async Task<IActionResult> New()
    {
        try
        {
            var (errorResult, _) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var categories = await BuildCategoryOptionsAsync();
            if (categories.Count == 0)
            {
                SetInfo("No active budget year with categories exists. Please contact a FinanceAdmin.");
                return RedirectToAction(nameof(Index));
            }

            return View(new ExpenseNewViewModel { Categories = categories });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading new expense report form");
            SetError("Failed to load the form.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("New")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(ExpenseNewViewModel model)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            if (!ModelState.IsValid)
            {
                model.Categories = await BuildCategoryOptionsAsync();
                return View(model);
            }

            var id = await _service.CreateDraftAsync(user.Id, model.BudgetCategoryId, model.Note);
            SetSuccess("Draft created.");
            return RedirectToAction(nameof(Edit), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating draft expense report for user {UserId}", user.Id);
            SetError("Failed to create draft.");
            model.Categories = await BuildCategoryOptionsAsync();
            return View(model);
        }
    }

    // ───────────────────────────── 6.3  Detail ───────────────────────────────

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var report = await _service.GetAsync(id);
            if (report is null) return NotFound();

            // Phase 6: submitter-only access. Phase 7 will add coordinator + FinanceAdmin
            // via ExpenseReportAuthorizationHandler.
            if (report.SubmitterUserId != user.Id) return Forbid();

            var profile = await _profileService.GetProfileAsync(user.Id);
            var category = await _budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
            var categoryName = category is not null
                ? $"{category.BudgetGroup?.Name} / {category.Name}"
                : "(unknown category)";

            var editableStatuses = new[] { ExpenseReportStatus.Draft, ExpenseReportStatus.Submitted, ExpenseReportStatus.CoordinatorEndorsed };
            var withdrawableStatuses = new[] { ExpenseReportStatus.Draft, ExpenseReportStatus.Submitted, ExpenseReportStatus.CoordinatorEndorsed };

            var model = new ExpenseDetailViewModel
            {
                Report = report,
                CategoryDisplayName = categoryName,
                CanEdit = report.SubmitterUserId == user.Id && editableStatuses.Contains(report.Status),
                CanSubmit = report.Status == ExpenseReportStatus.Draft,
                CanWithdraw = withdrawableStatuses.Contains(report.Status),
                HasIban = !string.IsNullOrEmpty(profile?.Iban),
                MaskedIban = string.IsNullOrEmpty(profile?.Iban) ? null : IbanFormatter.Mask(profile.Iban)
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading expense report {ReportId}", id);
            SetError("Failed to load the expense report.");
            return RedirectToAction(nameof(Index));
        }
    }

    // ───────────────────────────── 6.4  Edit ─────────────────────────────────

    [HttpGet("{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var report = await _service.GetAsync(id);
            if (report is null) return NotFound();
            if (report.SubmitterUserId != user.Id) return Forbid();

            var editableStatuses = new[] { ExpenseReportStatus.Draft, ExpenseReportStatus.Submitted, ExpenseReportStatus.CoordinatorEndorsed };
            if (!editableStatuses.Contains(report.Status))
            {
                SetError("This report can no longer be edited.");
                return RedirectToAction(nameof(Detail), new { id });
            }

            var categories = await BuildCategoryOptionsAsync();
            var model = new ExpenseEditViewModel
            {
                Report = report,
                Categories = categories,
                CanEditHeader = true,
                CanEditLines = report.Status == ExpenseReportStatus.Draft,
                BudgetCategoryId = report.BudgetCategoryId,
                Note = report.Note
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading edit form for report {ReportId}", id);
            SetError("Failed to load the edit form.");
            return RedirectToAction(nameof(Detail), new { id });
        }
    }

    [HttpPost("{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ExpenseEditViewModel model)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        try
        {
            if (!ModelState.IsValid)
            {
                model.Report = report;
                model.Categories = await BuildCategoryOptionsAsync();
                model.CanEditHeader = true;
                model.CanEditLines = report.Status == ExpenseReportStatus.Draft;
                return View(model);
            }

            await _service.UpdateDraftAsync(id, user.Id, model.BudgetCategoryId, model.Note);
            SetSuccess("Report updated.");
            return RedirectToAction(nameof(Edit), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating expense report {ReportId}", id);
            SetError($"Failed to update: {ex.Message}");
            model.Report = report;
            model.Categories = await BuildCategoryOptionsAsync();
            model.CanEditHeader = true;
            model.CanEditLines = report.Status == ExpenseReportStatus.Draft;
            return View(model);
        }
    }

    // ─────────────────────── 6.4 (continued) — Line add/edit/remove ──────────

    [HttpPost("{id:guid}/Lines/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLine(Guid id, AddLineInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid line data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        try
        {
            await _service.AddLineAsync(id, user.Id, input.Description, input.Amount);
            SetSuccess("Line added.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding line to report {ReportId}", id);
            SetError($"Failed to add line: {ex.Message}");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLine(Guid id, EditLineInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid line data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        try
        {
            await _service.UpdateLineAsync(id, user.Id, input.LineId, input.Description, input.Amount);
            SetSuccess("Line updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating line {LineId} on report {ReportId}", input.LineId, id);
            SetError($"Failed to update line: {ex.Message}");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLine(Guid id, Guid lineId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        try
        {
            // Service.RemoveLineAsync handles line + attachment row removal.
            // Delete the on-disk file separately (storage is not a repository).
            var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line?.Attachment is not null)
            {
                try
                {
                    await _storage.DeleteAsync(line.Attachment.Id, line.Attachment.Extension);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not delete attachment file {AttachmentId} while removing line {LineId}",
                        line.Attachment.Id, lineId);
                }
            }

            await _service.RemoveLineAsync(id, user.Id, lineId);
            SetSuccess("Line removed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing line {LineId} from report {ReportId}", lineId, id);
            SetError($"Failed to remove line: {ex.Message}");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    // ─────────────────────── 6.4 (continued) — Attachment upload/remove ──────

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Attach")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25 MB limit on request; storage service enforces 20 MB
    public async Task<IActionResult> AttachFile(Guid id, Guid lineId, IFormFile? file)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (file is null || file.Length == 0)
        {
            SetError("Please select a file.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (file.Length > AttachmentMaxBytes)
        {
            SetError($"File too large. Maximum size is {AttachmentMaxBytes / (1024 * 1024)} MB.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (!AllowedContentTypes.Contains(file.ContentType))
        {
            SetError("Unsupported file type. Upload PDF, JPEG, PNG, or HEIC.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (!ExtensionByContentType.TryGetValue(file.ContentType, out var extension))
            extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        try
        {
            await using var stream = file.OpenReadStream();
            var attachmentId = await _storage.StoreAsync(stream, extension, file.ContentType);

            var attachment = new ExpenseAttachment
            {
                Id = attachmentId,
                OriginalFileName = Path.GetFileName(file.FileName),
                Extension = extension,
                ContentType = file.ContentType,
                SizeBytes = file.Length,
                UploadedByUserId = user.Id,
                UploadedAt = _clock.GetCurrentInstant()
            };
            await _repo.AddAttachmentAsync(attachment);
            await _service.AttachToLineAsync(id, user.Id, lineId, attachmentId);
            SetSuccess("Attachment uploaded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading attachment for line {LineId} on report {ReportId}", lineId, id);
            SetError($"Failed to upload attachment: {ex.Message}");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/RemoveAttachment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAttachment(Guid id, Guid lineId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        try
        {
            var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line?.Attachment is not null)
            {
                try
                {
                    await _storage.DeleteAsync(line.Attachment.Id, line.Attachment.Extension);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not delete attachment file {AttachmentId}", line.Attachment.Id);
                }
                // Unlink first, then delete the row
                await _repo.SetLineAttachmentAsync(lineId, null);
                await _repo.RemoveAttachmentAsync(line.Attachment.Id);
            }
            SetSuccess("Attachment removed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing attachment from line {LineId} on report {ReportId}", lineId, id);
            SetError($"Failed to remove attachment: {ex.Message}");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    // ───────────────────────────── 6.5  Submit ───────────────────────────────

    [HttpPost("{id:guid}/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        try
        {
            var ok = await _service.SubmitAsync(id, user.Id);
            if (ok)
            {
                SetSuccess("Report submitted.");
                return RedirectToAction(nameof(Detail), new { id });
            }

            SetError("Could not submit the report. Make sure it has at least one line with an attachment and your payment IBAN is set.");
            return RedirectToAction(nameof(Detail), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting expense report {ReportId}", id);
            SetError($"Submission failed: {ex.Message}");
            return RedirectToAction(nameof(Detail), new { id });
        }
    }

    // ───────────────────────────── 6.6  Withdraw ─────────────────────────────

    [HttpPost("{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        try
        {
            var ok = await _service.WithdrawAsync(id, user.Id);
            if (ok)
            {
                SetSuccess("Report withdrawn.");
            }
            else
            {
                SetError("Could not withdraw this report.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error withdrawing expense report {ReportId}", id);
            SetError($"Withdrawal failed: {ex.Message}");
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    // ───────────────────────────── 6.7  IBAN modal ───────────────────────────

    [HttpGet("{id:guid}/Iban")]
    public async Task<IActionResult> Iban(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var report = await _service.GetAsync(id);
            if (report is null) return NotFound();
            // Only the submitter may set their own IBAN via this route
            if (report.SubmitterUserId != user.Id) return Forbid();

            var profile = await _profileService.GetProfileAsync(user.Id);
            var model = new ExpenseIbanViewModel
            {
                ReportId = id,
                HasIban = !string.IsNullOrEmpty(profile?.Iban),
                MaskedIban = string.IsNullOrEmpty(profile?.Iban) ? null : IbanFormatter.Mask(profile.Iban)
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading IBAN modal for report {ReportId}", id);
            SetError("Failed to load IBAN form.");
            return RedirectToAction(nameof(Detail), new { id });
        }
    }

    [HttpPost("{id:guid}/Iban")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Iban(Guid id, ExpenseIbanViewModel model)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await _service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        // An empty/null IBAN value means "remove"
        var ibanValue = string.IsNullOrWhiteSpace(model.Iban) ? null : model.Iban.Trim();

        if (ibanValue is not null && !IbanValidator.IsValid(ibanValue))
        {
            ModelState.AddModelError(nameof(model.Iban), "Invalid IBAN format.");
            var profile = await _profileService.GetProfileAsync(user.Id);
            model.ReportId = id;
            model.HasIban = !string.IsNullOrEmpty(profile?.Iban);
            model.MaskedIban = string.IsNullOrEmpty(profile?.Iban) ? null : IbanFormatter.Mask(profile.Iban);
            return View(model);
        }

        try
        {
            await _profileService.SetIbanAsync(user.Id, ibanValue);

            if (ibanValue is null)
                SetSuccess("IBAN removed.");
            else
                SetSuccess("IBAN saved.");

            return RedirectToAction(nameof(Detail), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting IBAN for user {UserId}", user.Id);
            SetError("Failed to save IBAN.");
            model.ReportId = id;
            return View(model);
        }
    }

    // ───────────────────────────── 6.8  Attachment stream ────────────────────

    [HttpGet("Attachment/{attachmentId:guid}")]
    public async Task<IActionResult> Attachment(Guid attachmentId)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var attachment = await _repo.GetAttachmentByIdAsync(attachmentId);
            if (attachment is null) return NotFound();

            // Re-check: only the submitter of the owning report may access.
            // TODO Phase 7: also allow coordinator of the report's category and FinanceAdmin/Admin
            // via ExpenseReportAuthorizationHandler.
            var owningReport = await FindReportOwningAttachmentAsync(attachmentId, user.Id);
            if (owningReport is null) return Forbid();

            var stream = await _storage.OpenReadAsync(attachment.Id, attachment.Extension);
            return File(stream, attachment.ContentType, attachment.OriginalFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming attachment {AttachmentId}", attachmentId);
            return NotFound();
        }
    }

    // ──────────────────────────── Private helpers ─────────────────────────────

    private async Task<IReadOnlyList<BudgetCategoryOption>> BuildCategoryOptionsAsync()
    {
        var activeYear = await _budgetService.GetActiveYearAsync();
        if (activeYear is null) return [];

        return activeYear.Groups
            .OrderBy(g => g.SortOrder)
            .SelectMany(g => g.Categories
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new BudgetCategoryOption(c.Id, g.Name, c.Name)))
            .ToList();
    }

    /// <summary>
    /// Returns the report if the given user is its submitter and the report
    /// has a line whose attachment matches <paramref name="attachmentId"/>.
    /// Returns null if no such match (access denied).
    /// </summary>
    private async Task<ExpenseReportDto?> FindReportOwningAttachmentAsync(Guid attachmentId, Guid userId)
    {
        var reports = await _service.GetForSubmitterAsync(userId);
        return reports.FirstOrDefault(r =>
            r.Lines.Any(l => l.AttachmentId == attachmentId || l.Attachment?.Id == attachmentId));
    }
}
