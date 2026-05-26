using System.Text;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Authorization.Requirements;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Expenses")]
public sealed class ExpensesController(
    IUserServiceRead userService,
    IExpenseReportService service,
    IBudgetService budgetService,
    IClock clock,
    IAuthorizationService authService,
    ISepaPaymentFileBuilder sepaBuilder,
    IOptions<SepaConfig> sepaConfig,
    ILogger<ExpensesController> logger) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var reports = await service.GetForSubmitterAsync(user.Id);
            var activeYear = await budgetService.GetActiveYearAsync();
            var info = await _userService.GetUserInfoAsync(user.Id);

            var categoryNames = activeYear?.Groups
                .SelectMany(g => g.Categories.Select(c => (c.Id, Display: $"{g.Name} / {c.Name}")))
                .ToDictionary(x => x.Id, x => x.Display)
                ?? new Dictionary<Guid, string>();

            var model = new ExpensesIndexViewModel
            {
                Reports = reports,
                HasActiveYear = activeYear is not null,
                HasIban = !string.IsNullOrEmpty(info?.Profile?.Iban),
                CategoryNames = categoryNames,
            };
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading expense reports index for user");
            SetError("Failed to load expense reports.");
            return View(new ExpensesIndexViewModel
            {
                Reports = [],
                HasActiveYear = false,
                HasIban = false
            });
        }
    }

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
            logger.LogError(ex, "Error loading new expense report form");
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

            var id = await service.CreateDraftAsync(user.Id, model.BudgetCategoryId, model.Note);
            SetSuccess("Draft created.");
            return RedirectToAction(nameof(Edit), new { id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating draft expense report for user {UserId}", user.Id);
            SetError("Failed to create draft.");
            model.Categories = await BuildCategoryOptionsAsync();
            return View(model);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var report = await service.GetAsync(id);
            if (report is null) return NotFound();

            var authResult = await authService.AuthorizeAsync(User, report,
                new ExpenseReportOperationRequirement(ExpenseReportOperation.View));
            if (!authResult.Succeeded) return Forbid();

            var detail = await service.GetDetailViewDataAsync(user.Id, report);
            var model = new ExpenseDetailViewModel
            {
                Report = report,
                CategoryDisplayName = detail.CategoryDisplayName,
                CanEdit = detail.CanEdit,
                CanSubmit = detail.CanSubmit,
                CanWithdraw = detail.CanWithdraw,
                HasIban = detail.HasIban,
                MaskedIban = detail.MaskedIban,
                HoldedTimeline = detail.HoldedTimeline
            };
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading expense report {ReportId}", id);
            SetError("Failed to load the expense report.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var report = await service.GetAsync(id);
            if (report is null) return NotFound();
            if (report.SubmitterUserId != user.Id) return Forbid();

            var editableStatuses = new[] { ExpenseReportStatus.Draft };
            if (!editableStatuses.Contains(report.Status))
            {
                SetError("This report can no longer be edited.");
                return RedirectToAction(nameof(Detail), new { id });
            }

            var model = new ExpenseEditViewModel
            {
                BudgetCategoryId = report.BudgetCategoryId,
                Note = report.Note
            };
            await PopulateEditModelAsync(model, report);
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading edit form for report {ReportId}", id);
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

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            await PopulateEditModelAsync(model, report);
            return View(model);
        }

        var result = await service.UpdateDraftWithResultAsync(id, user.Id, model.BudgetCategoryId, model.Note);
        if (result.Succeeded)
        {
            SetSuccess("Report updated.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        SetError($"Failed to update: {result.ErrorMessage}");
        await PopulateEditModelAsync(model, report);
        return View(model);
    }

    [HttpPost("{id:guid}/Lines/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLine(Guid id, AddLineInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid line data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await service.AddLineWithResultAsync(id, user.Id, input.Description, input.Amount);
        if (!result.Succeeded)
            SetError($"Failed to add line: {result.ErrorMessage}");
        else
            SetSuccess("Line added.");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLine(Guid id, EditLineInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid line data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await service.UpdateLineWithResultAsync(id, user.Id, input.LineId, input.Description, input.Amount);
        if (!result.Succeeded)
            SetError($"Failed to update line: {result.ErrorMessage}");
        else
            SetSuccess("Line updated.");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLine(Guid id, Guid lineId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        var result = await service.RemoveLineWithResultAsync(id, user.Id, lineId);
        if (!result.Succeeded)
            SetError($"Failed to remove line: {result.ErrorMessage}");
        else
            SetSuccess("Line removed.");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Attach")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25 MB limit on request; service enforces 20 MB + content type
    public async Task<IActionResult> AttachFile(Guid id, Guid lineId, IFormFile? file)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (file is null || file.Length == 0)
        {
            SetError("Please select a file.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        await using var stream = file.OpenReadStream();
        var result = await service.AttachFileToLineWithResultAsync(
            id, user.Id, lineId, file.FileName, file.ContentType, stream);

        if (result.Succeeded)
            SetSuccess("Attachment uploaded.");
        else
            SetError(result.ErrorMessage ?? "Failed to upload attachment.");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/RemoveAttachment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAttachment(Guid id, Guid lineId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        try
        {
            await service.RemoveAttachmentFromLineAsync(id, user.Id, lineId);
            SetSuccess("Attachment removed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing attachment from line {LineId} on report {ReportId}", lineId, id);
            SetError($"Failed to remove attachment: {ex.Message}");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        var result = await service.SubmitWithResultAsync(id, user.Id);
        if (result.Succeeded)
            SetSuccess("Report submitted.");
        else
            SetError(result.ErrorMessage ?? "Could not submit the report.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid(); var result = await service.WithdrawWithResultAsync(id, user.Id);
        if (result.Succeeded)
            SetSuccess("Report withdrawn.");
        else
            SetError(result.ErrorMessage ?? "Could not withdraw this report.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}/Iban")]
    public async Task<IActionResult> Iban(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var report = await service.GetAsync(id);
            if (report is null) return NotFound();
            if (report.SubmitterUserId != user.Id) return Forbid();

            var iban = await service.GetSubmitterIbanViewAsync(user.Id);
            var model = new ExpenseIbanViewModel
            {
                ReportId = id,
                HasIban = iban.HasIban,
                MaskedIban = iban.MaskedIban
            };
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading IBAN modal for report {ReportId}", id);
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

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        var result = await service.SaveSubmitterIbanWithResultAsync(user.Id, model.Iban);
        if (result.Succeeded)
        {
            SetSuccess(result.Message);
            return RedirectToAction(nameof(Detail), new { id });
        }

        if (result.IsValidationError)
            ModelState.AddModelError(nameof(model.Iban), result.Message);
        else
            SetError(result.Message);

        model.ReportId = id;
        model.HasIban = result.HasIban;
        model.MaskedIban = result.MaskedIban;
        return View(model);
    }

    [HttpGet("Attachment/{attachmentId:guid}")]
    public async Task<IActionResult> Attachment(Guid attachmentId)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            // Visibility = report's View handler grant. NotFound on both miss + denial (no leak).
            var owningReport = await service.GetReportOwningAttachmentAsync(attachmentId);
            if (owningReport is null) return NotFound();

            var authResult = await authService.AuthorizeAsync(User, owningReport,
                new ExpenseReportOperationRequirement(ExpenseReportOperation.View));
            if (!authResult.Succeeded) return NotFound();

            var attachment = await service.TryReadAttachmentAsync(owningReport, attachmentId);
            if (attachment is null) return NotFound();

            return File(attachment.Bytes, attachment.ContentType, attachment.OriginalFileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error streaming attachment {AttachmentId}", attachmentId);
            return NotFound();
        }
    }

    [HttpGet("Coordinator")]
    public async Task<IActionResult> Coordinator()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var reports = await service.GetCoordinatorQueueAsync(user.Id);
            var submitterNames = await ResolveSubmitterNamesAsync(reports);
            return View(new ExpenseCoordinatorViewModel { Reports = reports, SubmitterNames = submitterNames });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading coordinator queue");
            SetError("Failed to load the coordinator queue.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("{id:guid}/Endorse")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Endorse(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();

        var authResult = await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(ExpenseReportOperation.Endorse));
        if (!authResult.Succeeded) return Forbid();

        var result = await service.CoordinatorEndorseWithResultAsync(id, user.Id);
        if (result.Succeeded)
            SetSuccess("Report endorsed.");
        else
            SetError(result.ErrorMessage ?? "Could not endorse the report.");

        return RedirectToAction(nameof(Coordinator));
    }

    [HttpPost("{id:guid}/CoordinatorReject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CoordinatorReject(Guid id, CoordinatorRejectInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();

        var authResult = await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(ExpenseReportOperation.CoordinatorReject));
        if (!authResult.Succeeded) return Forbid();

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Reason))
        {
            SetError("A rejection reason is required.");
            return RedirectToAction(nameof(Coordinator));
        }

        var result = await service.CoordinatorRejectWithResultAsync(id, user.Id, input.Reason);
        if (result.Succeeded)
            SetSuccess("Report rejected.");
        else
            SetError(result.ErrorMessage ?? "Could not reject the report.");

        return RedirectToAction(nameof(Coordinator));
    }

    [HttpGet("Review")]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> Review()
    {
        try
        {
            var reports = await service.GetReviewQueueAsync();
            var submitterNames = await ResolveSubmitterNamesAsync(reports);
            return View(new ExpenseReviewViewModel { Reports = reports, SubmitterNames = submitterNames });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading finance admin review queue");
            SetError("Failed to load the review queue.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("{id:guid}/Approve")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> Approve(Guid id, ApproveInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();

        var authResult = await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(ExpenseReportOperation.Approve));
        if (!authResult.Succeeded) return Forbid();

        var result = await service.ApproveWithResultAsync(id, user.Id, input.OverrideCategoryId);
        if (result.Succeeded)
            SetSuccess("Report approved.");
        else
            SetError(result.ErrorMessage ?? "Could not approve the report.");

        return RedirectToAction(nameof(Review));
    }

    [HttpPost("{id:guid}/Reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> Reject(Guid id, FinanceRejectInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await service.GetAsync(id);
        if (report is null) return NotFound();

        var authResult = await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(ExpenseReportOperation.FinanceReject));
        if (!authResult.Succeeded) return Forbid();

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Reason))
        {
            SetError("A rejection reason is required.");
            return RedirectToAction(nameof(Review));
        }

        var result = await service.FinanceRejectWithResultAsync(id, user.Id, input.Reason);
        if (result.Succeeded)
            SetSuccess("Report rejected.");
        else
            SetError(result.ErrorMessage ?? "Could not reject the report.");

        return RedirectToAction(nameof(Review));
    }

    [HttpPost("Sepa/Generate")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> SepaGenerate(
        [FromForm] List<Guid> ids,
        CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (ids.Count == 0)
        {
            SetError("No reports selected.");
            return RedirectToAction(nameof(Review));
        }

        try
        {
            return await ExecuteSepaGenerateAsync(ids, user.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating SEPA pain.001 for user {UserId}", user.Id);
            SetError("Failed to generate SEPA file.");
            return RedirectToAction(nameof(Review));
        }
    }

    private async Task<IActionResult> ExecuteSepaGenerateAsync(
        List<Guid> ids, Guid actorUserId, CancellationToken ct)
    {
        var eligible = await ResolveEligibleForSepaAsync(ids, ct);
        if (eligible.Count == 0)
        {
            SetError("None of the selected reports could be included in the SEPA payout. Reports must be in Approved status.");
            return RedirectToAction(nameof(Review));
        }

        // XML before flip so failure at either step leaves a retry-safe state.
        var now = clock.GetCurrentInstant();
        var xml = sepaBuilder.BuildPain001(sepaConfig.Value, now, eligible);

        var flippedIds = await service.MarkSepaSentAsync(
            eligible.Select(r => r.Id).ToList(), actorUserId, ct);
        if (flippedIds.Count == 0)
        {
            SetError("No reports were transitioned to SEPA Sent. They may have already been processed.");
            return RedirectToAction(nameof(Review));
        }

        var fileName = $"sepa-{now.InUtc().LocalDateTime:yyyy-MM-dd-HHmm}.xml";

        logger.LogInformation(
            "SEPA pain.001 generated by {UserId}: {EligibleCount} eligible, {FlippedCount} flipped to SepaSent",
            actorUserId, eligible.Count, flippedIds.Count);

        return File(Encoding.UTF8.GetBytes(xml), "application/xml", fileName);
    }

    private async Task<List<ExpenseReportDto>> ResolveEligibleForSepaAsync(
        IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = new List<ExpenseReportDto>();
        foreach (var id in ids)
        {
            var report = await service.GetAsync(id, ct);
            if (report is null || report.Status != ExpenseReportStatus.Approved) continue;

            var authResult = await authService.AuthorizeAsync(User, report,
                new ExpenseReportOperationRequirement(ExpenseReportOperation.IncludeInSepaPayout));
            if (authResult.Succeeded) result.Add(report);
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveSubmitterNamesAsync(
        IReadOnlyCollection<ExpenseReportDto> reports)
    {
        var ids = reports.Select(r => r.SubmitterUserId).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var users = await _userService.GetUserInfosAsync(ids);
        return ids.ToDictionary(
            id => id,
            id => users.TryGetValue(id, out var u) && !string.IsNullOrWhiteSpace(u.BurnerName)
                ? u.BurnerName
                : "(unknown)");
    }

    private async Task PopulateEditModelAsync(ExpenseEditViewModel model, ExpenseReportDto report)
    {
        model.Report = report;
        model.Categories = await BuildCategoryOptionsAsync();
        model.CanEditHeader = true;
        model.CanEditLines = report.Status == ExpenseReportStatus.Draft;
    }

    private async Task<IReadOnlyList<BudgetCategoryOption>> BuildCategoryOptionsAsync()
    {
        var activeYear = await budgetService.GetActiveYearAsync();
        if (activeYear is null) return [];

        return activeYear.Groups
            .OrderBy(g => g.SortOrder)
            .SelectMany(g => g.Categories
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new BudgetCategoryOption(c.Id, g.Name, c.Name)))
            .ToList();
    }

}
