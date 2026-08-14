using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Services;
using Humans.Finance.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Expenses.Services.Dtos;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.UI.Authorization;
using Humans.UI.Controllers;
using Humans.Expenses.Authorization;
using Humans.Expenses.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Expenses.Controllers;

[Authorize]
[Route("Expenses")]
internal sealed class ExpensesController(
    IUserServiceRead userService,
    IExpenseReportServiceRead expenseReadService,
    IExpenseReportService service,
    IBudgetServiceRead budgetService,
    IHoldedFinanceService holdedFinance,
    IAuthorizationService authService,
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

            var reports = await expenseReadService.GetForSubmitterAsync(user.Id);
            var activeYear = await budgetService.GetActiveYearAsync();
            var info = await _userService.GetUserInfoAsync(user.Id);

            var categoryNames = activeYear?.Groups
                .SelectMany(g => g.Categories.Select(c => (c.Id, Display: $"{g.Name} / {c.Name}")))
                .ToDictionary(x => x.Id, x => x.Display)
                ?? new Dictionary<Guid, string>();

            var coordinatorQueue = await expenseReadService.GetCoordinatorQueueAsync(user.Id);
            var coordinatorTeamIds = await budgetService.GetEffectiveCoordinatorTeamIdsAsync(user.Id);

            // The member's own Holded creditor-account statement (read-only, real ledger lines). Own
            // account only. The binding is tracked separately from the ledger: GetCreditorLedgerAsync
            // returns null both for "not bound" and for "bound, but no journal activity cached yet",
            // and telling a correctly-bound member to go get bound again is worse than saying nothing.
            HoldedCreditorLedger? accountLedger = null;
            var binding = await holdedFinance.GetCreditorContactByUserAsync(user.Id);
            var boundAccountNum = binding?.SupplierAccountNum;
            if (boundAccountNum is { } accNum)
            {
                var led = await holdedFinance.GetCreditorLedgerAsync(accNum);
                if (led is not null)
                    accountLedger = led with
                    {
                        Lines = led.Lines
                            .OrderByDescending(l => l.Date)
                            .ThenByDescending(l => l.EntryNumber)
                            .ThenBy(l => l.Line)
                            .ToList()
                    };
            }

            var model = new ExpensesIndexViewModel
            {
                Reports = reports,
                HasActiveYear = activeYear is not null,
                HasIban = !string.IsNullOrEmpty(info?.Profile?.Iban),
                CategoryNames = categoryNames,
                AccountLedger = accountLedger,
                BoundAccountNum = boundAccountNum,
                IsCoordinator = coordinatorTeamIds.Count > 0,
                CoordinatorQueueCount = coordinatorQueue.Count,
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

            var report = await expenseReadService.GetAsync(id);
            if (report is null) return NotFound();

            var authResult = await authService.AuthorizeAsync(User, report,
                new ExpenseReportOperationRequirement(ExpenseReportOperation.View));
            if (!authResult.Succeeded) return Forbid();

            var category = await budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
            var categoryName = category is not null
                ? $"{category.BudgetGroup?.Name} / {category.Name}"
                : "(unknown category)";
            var isSubmitter = report.SubmitterUserId == user.Id;
            var canWithdraw = report.Status is ExpenseReportStatus.Submitted
                or ExpenseReportStatus.CoordinatorEndorsed
                or ExpenseReportStatus.Approved;
            // The viewer's own profile IBAN drives only their own draft's Set/Change flow. Loading it for
            // someone else's report would answer "has the *submitter* got payment details" with the
            // viewer's answer — see PayeeName/PayeeIban below for the submitter's own.
            var iban = isSubmitter ? await GetIbanViewAsync(user.Id) : (HasIban: false, MaskedIban: null);

            // Finance admins reviewing a report can bind the submitter to a Holded creditor account
            // before approval, so the push reuses the right 400000xx instead of minting a duplicate.
            var isFinanceAdmin = (await authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;
            // The submitter reads the payment half of the timeline; the finance admin reads the push
            // half — and is the only one who can act on a failed push, so withholding it from them
            // was backwards (nobodies-collective/Humans#1045).
            var timeline = isSubmitter || isFinanceAdmin
                ? await expenseReadService.GetHoldedTimelineAsync(report)
                : null;
            var creditor = await GetCreditorBindingViewAsync(report.SubmitterUserId, isFinanceAdmin);

            var model = new ExpenseDetailViewModel
            {
                Report = report,
                CategoryDisplayName = categoryName,
                CanEdit = isSubmitter && report.Status == ExpenseReportStatus.Draft,
                CanSubmit = isSubmitter && report.Status == ExpenseReportStatus.Draft,
                CanWithdraw = isSubmitter && canWithdraw,
                IsSubmitter = isSubmitter,
                HasIban = iban.HasIban,
                MaskedIban = iban.MaskedIban,
                HoldedTimeline = timeline,
                CanBindCreditor = isFinanceAdmin,
                BoundAccountNum = creditor.BoundAccountNum,
                BoundAccountName = creditor.BoundAccountName,
                HasCreditorContact = creditor.HasContact,
                CreditorAccounts = creditor.Accounts,
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

            var report = await expenseReadService.GetAsync(id);
            if (report is null) return NotFound();
            if (report.SubmitterUserId != user.Id) return Forbid();

            if (report.Status != ExpenseReportStatus.Draft)
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

        var report = await expenseReadService.GetAsync(id);
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

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid line data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await service.AddLineWithResultAsync(id, user.Id, input.Description, input.Amount);
        SetMutationResultWithDetails(result, "Line added.", "Failed to add line");

        return RedirectToAction(nameof(Edit), new { id });
    }

    // Mileage and per-diem lines can no longer be created: the Add mileage / Add per diem forms and
    // their POST endpoints are gone. The service-layer plumbing
    // (AddMileageLineWithResultAsync / AddPerDiemLineWithResultAsync, ExpenseLineType.Mileage/PerDiem,
    // TravelReimbursementConfig) is retained so existing travel lines keep rendering and so the
    // feature can be turned back on by restoring the two actions and the two Edit.cshtml forms.

    [HttpPost("{id:guid}/Lines/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLine(Guid id, EditLineInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid line data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await service.UpdateLineWithResultAsync(id, user.Id, input.LineId, input.Description, input.Amount);
        SetMutationResultWithDetails(result, "Line updated.", "Failed to update line");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLine(Guid id, Guid lineId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        var result = await service.RemoveLineWithResultAsync(id, user.Id, lineId);
        SetMutationResultWithDetails(result, "Line removed.", "Failed to remove line");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Attach")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25 MB limit on request; service enforces 20 MB + content type
    public async Task<IActionResult> AttachFile(Guid id, Guid lineId, IFormFile? file)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
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

        SetMutationResult(result, "Attachment uploaded.", "Failed to upload attachment.");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/RemoveAttachment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAttachment(Guid id, Guid lineId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
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

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        var result = await service.SubmitWithResultAsync(id, user.Id);
        SetMutationResult(result, "Report submitted.", "Could not submit the report.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        var result = await service.WithdrawWithResultAsync(id, user.Id);
        SetMutationResult(result, "Report withdrawn.", "Could not withdraw this report.");
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}/Iban")]
    public async Task<IActionResult> Iban(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var report = await expenseReadService.GetAsync(id);
            if (report is null) return NotFound();
            if (report.SubmitterUserId != user.Id) return Forbid();

            var iban = await GetIbanViewAsync(user.Id);
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

        var report = await expenseReadService.GetAsync(id);
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

        var iban = await GetIbanViewAsync(user.Id);
        model.ReportId = id;
        model.HasIban = iban.HasIban;
        model.MaskedIban = iban.MaskedIban;
        return View(model);
    }

    [HttpGet("Attachment/{attachmentId:guid}")]
    public async Task<IActionResult> Attachment(Guid attachmentId)
    {
        try
        {
            var (errorResult, _) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            // Visibility = report's View handler grant. NotFound on both miss + denial (no leak).
            var owningReport = await expenseReadService.GetReportOwningAttachmentAsync(attachmentId);
            if (owningReport is null) return NotFound();

            var authResult = await authService.AuthorizeAsync(User, owningReport,
                new ExpenseReportOperationRequirement(ExpenseReportOperation.View));
            if (!authResult.Succeeded) return NotFound();

            var attachment = await expenseReadService.TryReadAttachmentAsync(owningReport, attachmentId);
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

            var reports = await expenseReadService.GetCoordinatorQueueAsync(user.Id);
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

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();

        var authResult = await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(ExpenseReportOperation.Endorse));
        if (!authResult.Succeeded) return Forbid();

        var result = await service.CoordinatorEndorseWithResultAsync(id, user.Id);
        SetMutationResult(result, "Report endorsed.", "Could not endorse the report.");

        return RedirectToAction(nameof(Coordinator));
    }

    [HttpPost("{id:guid}/CoordinatorReject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CoordinatorReject(Guid id, CoordinatorRejectInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
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
        SetMutationResult(result, "Report rejected.", "Could not reject the report.");

        return RedirectToAction(nameof(Coordinator));
    }

    [HttpGet("Review")]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> Review()
    {
        try
        {
            var reports = await expenseReadService.GetReviewQueueAsync();
            var submitterNames = await ResolveSubmitterNamesAsync(reports);
            return View(new ExpenseReviewViewModel
            {
                Reports = reports,
                SubmitterNames = submitterNames,
                FailedHoldedPushCount = await service.CountFailedHoldedPushesAsync(),
            });
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

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();

        var authResult = await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(ExpenseReportOperation.Approve));
        if (!authResult.Succeeded) return Forbid();

        var result = await service.ApproveWithResultAsync(id, user.Id, input.OverrideCategoryId);
        SetMutationResult(result, "Report approved.", "Could not approve the report.");

        return RedirectToAction(nameof(Review));
    }

    [HttpPost("{id:guid}/Reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> Reject(Guid id, FinanceRejectInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
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
        SetMutationResult(result, "Report rejected.", "Could not reject the report.");

        return RedirectToAction(nameof(Review));
    }

    [HttpPost("{id:guid}/HoldedRetry")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> HoldedRetry(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();

        var authResult = await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(ExpenseReportOperation.RequeueHoldedPush));
        if (!authResult.Succeeded) return Forbid();

        var result = await service.RequeueHoldedPushWithResultAsync(id, user.Id);
        SetMutationResult(result,
            "Holded push re-queued — it runs on the next drain pass.",
            "Could not re-queue the Holded push.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>
    /// Finance admins reviewing a report can bind the submitter to a Holded creditor account before
    /// approval, so the push reuses the right 400000xx instead of minting a duplicate. Empty for
    /// everyone else — the binding is not theirs to see.
    /// </summary>
    private async Task<(int? BoundAccountNum, string? BoundAccountName, bool HasContact,
        IReadOnlyList<HoldedCreditorAccountRow> Accounts)> GetCreditorBindingViewAsync(
        Guid submitterUserId, bool isFinanceAdmin)
    {
        if (!isFinanceAdmin) return (null, null, false, []);

        var binding = await holdedFinance.GetCreditorContactByUserAsync(submitterUserId);
        var accounts = (await holdedFinance.ListCreditorAccountsAsync()).Accounts
            .OrderBy(a => a.SupplierAccountNum)
            .ToList();

        var boundName = binding?.SupplierAccountNum is { } boundNum
            ? accounts.FirstOrDefault(a => a.SupplierAccountNum == boundNum)?.Name
            : null;

        return (binding?.SupplierAccountNum, boundName, binding is not null, accounts);
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

    private void SetMutationResult(
        ExpenseMutationResult result, string successMessage, string fallbackErrorMessage)
    {
        if (result.Succeeded)
            SetSuccess(successMessage);
        else
            SetError(result.ErrorMessage ?? fallbackErrorMessage);
    }

    private void SetMutationResultWithDetails(
        ExpenseMutationResult result, string successMessage, string errorPrefix)
    {
        if (result.Succeeded)
            SetSuccess(successMessage);
        else
            SetError(result.ErrorMessage is null ? errorPrefix : $"{errorPrefix}: {result.ErrorMessage}");
    }

    private async Task<(bool HasIban, string? MaskedIban)> GetIbanViewAsync(Guid userId)
    {
        var iban = (await _userService.GetUserInfoAsync(userId))?.Profile?.Iban;
        var hasIban = !string.IsNullOrEmpty(iban);
        return (hasIban, hasIban ? IbanFormatter.Mask(iban!) : null);
    }

}
