using System.Globalization;
using Humans.Base.Extensions;
using Humans.Budget.Contracts;
using Humans.Expenses.Authorization;
using Humans.Expenses.Contracts;
using Humans.Expenses.Services;
using Humans.Finance.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Expenses.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Expenses.Controllers;

[Authorize]
[Route("Expenses")]
internal sealed class ExpensesController(
    IUserService userService,
    IExpenseReportManagement service,
    IBudgetService budgetService,
    IHoldedFinanceService holdedFinance,
    IPreapprovalService preapprovals,
    IAuthorizationService authService,
    IClock clock,
    ILogger<ExpensesController> logger,
    IStringLocalizer<ExpensesResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    /// <summary>Pre-approval claim deadlines are Europe/Madrid dates, the zone the association books in.</summary>
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            var reports = await service.GetForSubmitterAsync(user.Id, HttpContext.RequestAborted);
            var activeYear = await budgetService.GetActiveYearAsync(HttpContext.RequestAborted);
            var info = await _userService.GetUserInfoAsync(user.Id, HttpContext.RequestAborted);

            var bookingLabels = await ResolveBookingLabelsAsync(reports, HttpContext.RequestAborted);

            var coordinatorQueue = await service.GetCoordinatorQueueAsync(user.Id, HttpContext.RequestAborted);
            var coordinatorTeamIds = await budgetService.GetEffectiveCoordinatorTeamIdsAsync(user.Id, HttpContext.RequestAborted);
            var openGrants = (await preapprovals.ListClaimableForUserAsync(user.Id, HttpContext.RequestAborted))
                .OrderBy(g => g.ClaimBy).ThenBy(g => g.ProgramName, StringComparer.OrdinalIgnoreCase).ToList();

            // The member's own Holded creditor-account statement (read-only, real ledger lines). Own
            // account only. The binding is tracked separately from the ledger: GetCreditorLedgerAsync
            // returns null both for "not bound" and for "bound, but no journal activity cached yet",
            // and telling a correctly-bound member to go get bound again is worse than saying nothing.
            HoldedCreditorLedger? accountLedger = null;
            var binding = await holdedFinance.GetCreditorContactByUserAsync(user.Id, HttpContext.RequestAborted);
            var boundAccountNum = binding?.SupplierAccountNum;
            if (boundAccountNum is { } accNum)
            {
                var led = await holdedFinance.GetCreditorLedgerAsync(accNum, HttpContext.RequestAborted);
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
                BookingLabels = bookingLabels,
                OpenGrants = openGrants,
                Today = Today,
                AccountLedger = accountLedger,
                BoundAccountNum = boundAccountNum,
                // A claim waiting on this viewer's sign-off always earns the link.
                IsCoordinator = coordinatorTeamIds.Count > 0 || coordinatorQueue.Count > 0,
                CoordinatorQueueCount = coordinatorQueue.Count,
            };
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading expense reports index for user");
            SetError(localizer["Expenses_Flash_LoadIndexFailed"]);
            return View(new ExpensesIndexViewModel
            {
                Reports = [],
                HasActiveYear = false,
                HasIban = false
            });
        }
    }

    /// <summary>The new-report form. <paramref name="grant"/> preselects that pre-approval (the
    /// "File expense" link on the index and on /Preapprovals). Any grant is accepted here so a
    /// finance admin can file on a member's behalf; the service only ever lets the submitter's own
    /// be claimed.</summary>
    [HttpGet("New")]
    public async Task<IActionResult> New(Guid? grant = null)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            var accounts = await BuildAccountOptionsAsync(HttpContext.RequestAborted);
            if (accounts.Count == 0)
            {
                SetInfo(localizer["Expenses_Flash_NoActiveYearCategories"]);
                return RedirectToAction(nameof(Index));
            }

            var model = new ExpenseNewViewModel
            {
                Accounts = accounts,
                SubmitterUserId = user.Id,
                PreapprovalGrantId = grant,
            };
            await PopulateGrantOptionsAsync(model, user.Id, HttpContext.RequestAborted);
            // The grant's label is the project name, which is what the Subject asks for.
            model.Note = model.Grants.FirstOrDefault(g => g.Id == grant)?.Label;
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading new expense report form");
            SetError(localizer["Expenses_Flash_LoadFormFailed"]);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("New")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(ExpenseNewViewModel model)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        // Blank means "me" — the picker only renders for finance admins, and clearing its search
        // box clears the hidden id.
        var submitterUserId = model.SubmitterUserId is { } picked && picked != Guid.Empty
            ? picked
            : user.Id;
        if (submitterUserId != user.Id && !await IsFinanceAdminAsync()) return Forbid();

        // A pre-approval brings its own account, so the account field is not required with one.
        if (model.PreapprovalGrantId is not null) ModelState.Remove(nameof(model.HoldedAccountNumber));

        try
        {
            if (!ModelState.IsValid)
            {
                model.Accounts = await BuildAccountOptionsAsync();
                await PopulateGrantOptionsAsync(model, user.Id);
                return View(model);
            }

            var result = await service.CreateDraftAsync(
                submitterUserId, user.Id, model.HoldedAccountNumber, model.Note, model.PreapprovalGrantId);
            if (result.Succeeded)
            {
                SetSuccess(localizer["Expenses_Flash_DraftCreated"]);
                return RedirectToAction(nameof(Edit), new { id = result.ReportId });
            }

            SetError(GetErrorMessage(result.Error) is { } reason
                ? $"{localizer["Expenses_Flash_CreateFailed"]} {reason}"
                : localizer["Expenses_Flash_CreateFailed"].Value);
            model.Accounts = await BuildAccountOptionsAsync();
            await PopulateGrantOptionsAsync(model, user.Id);
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating draft expense report for user {UserId}", submitterUserId);
            SetError(localizer["Expenses_Flash_CreateFailed"]);
            model.Accounts = await BuildAccountOptionsAsync();
            await PopulateGrantOptionsAsync(model, user.Id);
            return View(model);
        }
    }

    /// <summary>The grants the new form offers: the viewer's claimable ones, plus the preselected
    /// one when it is somebody else's (an admin filing on their behalf) so the form can show it.</summary>
    private async Task PopulateGrantOptionsAsync(ExpenseNewViewModel model, Guid viewerId, CancellationToken ct = default)
    {
        var grants = (await preapprovals.ListClaimableForUserAsync(viewerId, ct)).OrderBy(g => g.ClaimBy).ToList();
        if (model.PreapprovalGrantId is { } wanted && grants.All(g => g.Id != wanted)
            && await preapprovals.GetGrantAsync(wanted, ct) is { } other)
            grants.Insert(0, other);
        model.Grants = grants;
        model.Today = Today;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        try
        {
            var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.View, HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            var bookingLabel = (await ResolveBookingLabelsAsync([report], HttpContext.RequestAborted))[report.Id];
            var isSubmitter = report.SubmitterUserId == user.Id;
            var canEdit = await AllowsAsync(report, ExpenseReportOperation.Edit);
            // Always the *report's* IBAN state, never the viewer's — an admin setting it up for a
            // member needs the member's answer.
            var iban = isSubmitter || canEdit
                ? await GetIbanViewAsync(report.SubmitterUserId, HttpContext.RequestAborted)
                : (HasIban: false, MaskedIban: null);

            var isFinanceAdmin = await IsFinanceAdminAsync();
            var decisions = await ResolveDecisionsAsync(report);
            // The submitter reads the payment half of the timeline; the finance admin reads the push
            // half and is the only one who can act on a failed push.
            var timeline = isSubmitter || isFinanceAdmin
                ? await service.GetHoldedTimelineAsync(report, HttpContext.RequestAborted)
                : null;
            var creditor = await GetCreditorBindingViewAsync(report.SubmitterUserId, isFinanceAdmin, HttpContext.RequestAborted);
            var grant = report.PreapprovalGrantId is { } grantId
                ? await preapprovals.GetGrantAsync(grantId, HttpContext.RequestAborted)
                : null;

            // View is held only by the submitter, the category coordinator and finance admins, so
            // reaching this page already proves the viewer may see who the submitter is. IBAN stays
            // behind CanSeePayee.
            var submitterInfo = await _userService.GetUserInfoAsync(report.SubmitterUserId, HttpContext.RequestAborted);
            var profileLegalName = $"{submitterInfo?.Profile?.FirstName} {submitterInfo?.Profile?.LastName}".Trim();

            var model = new ExpenseDetailViewModel
            {
                Report = report,
                BookingLabel = bookingLabel,
                PreapprovalGrant = grant,
                Today = Today,
                CanEdit = canEdit,
                CanSubmit = await AllowsAsync(report, ExpenseReportOperation.Submit),
                CanWithdraw = await AllowsAsync(report, ExpenseReportOperation.Withdraw),
                IsSubmitter = isSubmitter,
                CanSeeNames = true,
                SubmitterBurnerName = string.IsNullOrWhiteSpace(submitterInfo?.BurnerName) ? null : submitterInfo!.BurnerName,
                SubmitterLegalName = !string.IsNullOrWhiteSpace(report.PayeeName)
                    ? report.PayeeName
                    : (profileLegalName.Length > 0 ? profileLegalName : null),
                HasIban = iban.HasIban,
                MaskedIban = iban.MaskedIban,
                HoldedTimeline = timeline,
                CanBindCreditor = isFinanceAdmin,
                BoundAccountNum = creditor.BoundAccountNum,
                BoundAccountName = creditor.BoundAccountName,
                HasCreditorContact = creditor.HasContact,
                CreditorAccounts = creditor.Accounts,
                CanApprove = decisions.Approve,
                CanFinanceReject = decisions.FinanceReject,
                CanEndorse = decisions.Endorse,
                CanCoordinatorReject = decisions.CoordinatorReject,
                CanDeclineLines = await AllowsAsync(report, ExpenseReportOperation.DeclineLine),
                // Only the approval form offers an account override, so the list is loaded only
                // for the one viewer who can act on it.
                Accounts = decisions.Approve ? await BuildAccountOptionsAsync(HttpContext.RequestAborted) : [],
            };
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading expense report {ReportId}", id);
            SetError(localizer["Expenses_Flash_LoadReportFailed"]);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id)
    {
        try
        {
            var (errorResult, user, report) = await RequireReportAsync(id, ct: HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;
            if (!await AllowsAsync(report, ExpenseReportOperation.Edit))
            {
                // Someone who may read the report but not change it is told why; everyone else is
                // refused outright.
                if (!await AllowsAsync(report, ExpenseReportOperation.View)) return Forbid();
                SetError(localizer["Expenses_Flash_NotEditable"]);
                return RedirectToAction(nameof(Detail), new { id });
            }

            var model = new ExpenseEditViewModel
            {
                HoldedAccountNumber = report.HoldedAccountNumber,
                Note = report.Note,
                PreapprovalGrantId = report.PreapprovalGrantId,
            };
            await PopulateEditModelAsync(model, report);
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading edit form for report {ReportId}", id);
            SetError(localizer["Expenses_Flash_LoadEditFailed"]);
            return RedirectToAction(nameof(Detail), new { id });
        }
    }

    [HttpPost("{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ExpenseEditViewModel model)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit);
        if (errorResult is not null) return errorResult;

        if (model.PreapprovalGrantId is not null) ModelState.Remove(nameof(model.HoldedAccountNumber));
        if (!ModelState.IsValid)
        {
            await PopulateEditModelAsync(model, report);
            return View(model);
        }

        var result = await service.UpdateDraftWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), model.HoldedAccountNumber, model.Note, model.PreapprovalGrantId);
        if (result.Succeeded)
        {
            SetSuccess(localizer["Expenses_Flash_ReportUpdated"]);
            return RedirectToAction(nameof(Edit), new { id });
        }

        SetError(GetErrorMessage(result.Error) is null
            ? localizer["Expenses_Flash_ReportUpdateFailed"]
            : $"{localizer["Expenses_Flash_ReportUpdateFailed"]} {GetErrorMessage(result.Error)}");
        await PopulateEditModelAsync(model, report);
        return View(model);
    }

    [HttpGet("{id:guid}/Lines/New")]
    public async Task<IActionResult> NewLine(Guid id, ExpenseLineType? type = null)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ct: HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;
        if (!await AllowsAsync(report, ExpenseReportOperation.Edit))
        {
            if (!await AllowsAsync(report, ExpenseReportOperation.View)) return Forbid();
            SetError(localizer["Expenses_Flash_NotEditable"]);
            return RedirectToAction(nameof(Detail), new { id });
        }

        var model = new ExpenseLineNewViewModel
        {
            ReportId = id,
            IsInvoice = type == ExpenseLineType.Invoice,
        };
        return type is null ? View("NewLineChoice", model) : View(model);
    }

    [HttpPost("{id:guid}/Lines/Add")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25 MB limit on request; service enforces 20 MB + content type
    public async Task<IActionResult> AddLine(Guid id, AddLineInputModel input, IFormFile? file)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit);
        if (errorResult is not null) return errorResult;

        // Where the submitter came from — and returns to on a validation error.
        IActionResult BackToForm() => input.ParentLineId is { } parent
            ? RedirectToAction(nameof(LineProofs), new { id, lineId = parent })
            : RedirectToAction(nameof(NewLine), new { id, type = input.LineType });

        if (!ModelState.IsValid)
        {
            SetError(localizer["Expenses_Flash_InvalidLineData"]);
            return BackToForm();
        }

        await using var stream = file?.OpenReadStream();
        var upload = file is null || stream is null
            ? null
            : new ExpenseFileUpload(file.FileName, file.ContentType, stream);

        var result = await service.AddLineWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(),
            input.Description, input.Amount, input.LineType, input.ParentLineId, upload);

        if (!result.Succeeded)
        {
            SetError(GetErrorMessage(result.Error) is null
                ? localizer["Expenses_Flash_AddLineFailed"]
                : $"{localizer["Expenses_Flash_AddLineFailed"]} {GetErrorMessage(result.Error)}");
            return BackToForm();
        }

        if (input.ParentLineId is { } parentId)
        {
            SetSuccess(localizer["Expenses_Flash_ReceiptAdded"]);
            return RedirectToAction(nameof(LineProofs), new { id, lineId = parentId });
        }
        if (input.LineType == ExpenseLineType.Invoice)
        {
            SetSuccess(localizer["Expenses_Flash_InvoiceAdded"]);
            return RedirectToAction(nameof(LineProofs), new { id, lineId = result.LineId });
        }
        SetSuccess(localizer["Expenses_Flash_LineAdded"]);
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("{id:guid}/Lines/{lineId:guid}")]
    public async Task<IActionResult> LineEdit(Guid id, Guid lineId)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ct: HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;
        var (allowed, canEditLines) = await ResolveLinePageAccessAsync(report, user.Id);
        if (!allowed) return Forbid();

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return NotFound();

        return View(new ExpenseLineEditViewModel
        {
            Report = report,
            Line = line,
            CanEditLines = canEditLines,
        });
    }

    [HttpGet("{id:guid}/Lines/{lineId:guid}/Proofs")]
    public async Task<IActionResult> LineProofs(Guid id, Guid lineId)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ct: HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;
        var (allowed, canEditLines) = await ResolveLinePageAccessAsync(report, user.Id);
        if (!allowed) return Forbid();

        var invoiceLine = report.Lines.FirstOrDefault(
            l => l.Id == lineId && l.LineType == ExpenseLineType.Invoice);
        if (invoiceLine is null) return NotFound();

        return View(new ExpenseLineProofsViewModel
        {
            Report = report,
            InvoiceLine = invoiceLine,
            Proofs = report.Lines
                .Where(l => l.ParentLineId == lineId)
                .OrderBy(l => l.SortOrder)
                .ToList(),
            CanEditLines = canEditLines,
        });
    }

    [HttpPost("{id:guid}/Lines/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLine(Guid id, EditLineInputModel input)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit);
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            SetError(localizer["Expenses_Flash_InvalidLineData"]);
            return RedirectToAction(nameof(LineEdit), new { id, lineId = input.LineId });
        }

        var result = await service.UpdateLineWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), input.LineId, input.Description, input.Amount);
        SetMutationResultWithDetails(result, localizer["Expenses_Flash_LineUpdated"], localizer["Expenses_Flash_UpdateLineFailed"]);

        return RedirectToAction(nameof(LineEdit), new { id, lineId = input.LineId });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLine(Guid id, Guid lineId)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit);
        if (errorResult is not null) return errorResult;

        // A removed proof row returns the submitter to its invoice's proofs page.
        var parentLineId = report.Lines.FirstOrDefault(l => l.Id == lineId)?.ParentLineId;

        var result = await service.RemoveLineWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), lineId);
        SetMutationResultWithDetails(result, localizer["Expenses_Flash_LineRemoved"], localizer["Expenses_Flash_RemoveLineFailed"]);

        return parentLineId is { } parent
            ? RedirectToAction(nameof(LineProofs), new { id, lineId = parent })
            : RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Attach")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25 MB limit on request; service enforces 20 MB + content type
    public async Task<IActionResult> AttachFile(Guid id, Guid lineId, IFormFile? file)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit);
        if (errorResult is not null) return errorResult;

        if (file is null || file.Length == 0)
        {
            SetError(localizer["Expenses_Flash_SelectFile"]);
            return RedirectToAction(nameof(LineEdit), new { id, lineId });
        }

        await using var stream = file.OpenReadStream();
        var result = await service.AttachFileToLineWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), lineId, file.FileName, file.ContentType, stream);

        SetMutationResult(result, localizer["Expenses_Flash_AttachmentUploaded"], localizer["Expenses_Flash_UploadAttachmentFailed"]);

        return RedirectToAction(nameof(LineEdit), new { id, lineId });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/RemoveAttachment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAttachment(Guid id, Guid lineId)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit);
        if (errorResult is not null) return errorResult;

        var result = await service.RemoveAttachmentFromLineAsync(id, user.Id, await IsFinanceAdminAsync(), lineId);
        SetMutationResult(result, localizer["Expenses_Flash_AttachmentRemoved"], localizer["Expenses_Flash_RemoveAttachmentFailed"]);
        return RedirectToAction(nameof(LineEdit), new { id, lineId });
    }

    [HttpPost("{id:guid}/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid id)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Submit);
        if (errorResult is not null) return errorResult;

        var result = await service.SubmitWithResultAsync(id, user.Id, await IsFinanceAdminAsync());
        SetMutationResult(result, localizer["Expenses_Flash_ReportSubmitted"], localizer["Expenses_Flash_SubmitFailed"]);

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Withdraw);
        if (errorResult is not null) return errorResult;

        var result = await service.WithdrawWithResultAsync(id, user.Id);
        SetMutationResult(result, localizer["Expenses_Flash_ReportWithdrawn"], localizer["Expenses_Flash_WithdrawFailed"]);
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}/Iban")]
    public async Task<IActionResult> Iban(Guid id)
    {
        try
        {
            var (errorResult, user, report) = await RequireReportAsync(id, ct: HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;
            if (!await CanSetReportIbanAsync(report, user.Id)) return Forbid();

            // The IBAN on this page is always the report submitter's — the page sets who gets paid
            // for this report, and that is never the viewer when an admin opens it.
            var iban = await GetIbanViewAsync(report.SubmitterUserId, HttpContext.RequestAborted);
            var model = new ExpenseIbanViewModel
            {
                ReportId = id,
                HasIban = iban.HasIban,
                MaskedIban = iban.MaskedIban,
                ReportStatus = report.Status,
                MemberName = report.SubmitterUserId == user.Id
                    ? null
                    : (await _userService.GetUserInfoAsync(report.SubmitterUserId, HttpContext.RequestAborted))?.BurnerName,
            };
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading IBAN modal for report {ReportId}", id);
            SetError(localizer["Expenses_Flash_LoadIbanFailed"]);
            return RedirectToAction(nameof(Detail), new { id });
        }
    }

    [HttpPost("{id:guid}/Iban")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Iban(Guid id, ExpenseIbanViewModel model)
    {
        var (errorResult, user, report) = await RequireReportAsync(id);
        if (errorResult is not null) return errorResult;
        if (!await CanSetReportIbanAsync(report, user.Id)) return Forbid();

        var result = await service.SaveSubmitterIbanWithResultAsync(
            id, user.Id, model.Iban);
        if (result.Succeeded)
        {
            SetSuccess(localizer[result.MessageKey]);
            return RedirectToAction(nameof(Detail), new { id });
        }

        if (result.IsValidationError)
            ModelState.AddModelError(nameof(model.Iban), localizer[result.MessageKey]);
        else
            SetError(localizer[result.MessageKey]);

        var iban = await GetIbanViewAsync(report.SubmitterUserId);
        model.ReportId = id;
        model.HasIban = iban.HasIban;
        model.MaskedIban = iban.MaskedIban;
        model.ReportStatus = report.Status;
        model.MemberName = report.SubmitterUserId == user.Id
            ? null
            : (await _userService.GetUserInfoAsync(report.SubmitterUserId))?.BurnerName;
        return View(model);
    }

    [HttpGet("Attachment/{attachmentId:guid}")]
    public Task<IActionResult> Attachment(Guid attachmentId)
        => StreamAttachmentAsync(attachmentId, inline: false);

    /// <summary>Same file, rendered in the browser tab (images + PDFs) instead of downloaded, so a
    /// reviewer can check receipts without saving them. Non-renderable types fall back to download.</summary>
    [HttpGet("Attachment/{attachmentId:guid}/View")]
    public Task<IActionResult> AttachmentView(Guid attachmentId)
        => StreamAttachmentAsync(attachmentId, inline: true);

    private async Task<IActionResult> StreamAttachmentAsync(Guid attachmentId, bool inline)
    {
        try
        {
            var (errorResult, _) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            // Visibility = report's View handler grant. NotFound on both miss + denial (no leak).
            var owningReport = await service.GetReportOwningAttachmentAsync(attachmentId, HttpContext.RequestAborted);
            if (owningReport is null) return NotFound();

            var authResult = await authService.AuthorizeAsync(User, owningReport,
                new ExpenseReportOperationRequirement(ExpenseReportOperation.View));
            if (!authResult.Succeeded) return NotFound();

            var attachment = await service.TryReadAttachmentAsync(owningReport, attachmentId, HttpContext.RequestAborted);
            if (attachment is null) return NotFound();

            // Inline only for the browser-renderable subset of the upload whitelist — no filename
            // means no Content-Disposition: attachment, so the browser shows it in the tab.
            if (inline && ExpenseAttachmentDto.IsInlineViewableType(attachment.ContentType))
                return File(attachment.Bytes, attachment.ContentType);

            return File(attachment.Bytes, attachment.ContentType, attachment.OriginalFileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error streaming attachment {AttachmentId}", attachmentId);
            return NotFound();
        }
    }

    [HttpPost("{id:guid}/Endorse")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Endorse(Guid id, EndorseInputModel input)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Endorse);
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            SetError(localizer["Expenses_Flash_InvalidMaximum"]);
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await service.CoordinatorEndorseWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), input.MaxAmount);
        SetMutationResult(result, localizer["Expenses_Flash_ReportEndorsed"], localizer["Expenses_Flash_EndorseFailed"]);

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/CoordinatorReject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CoordinatorReject(Guid id, CoordinatorRejectInputModel input)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.CoordinatorReject);
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Reason))
        {
            SetError(localizer["Expenses_Flash_RejectionReasonRequired"]);
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await service.CoordinatorRejectWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), input.Reason);
        SetMutationResult(result, localizer["Expenses_Flash_ReportRejected"], localizer["Expenses_Flash_RejectFailed"]);

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/Decline")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclineLine(Guid id, Guid lineId, DeclineLineInputModel input)
    {
        var (errorResult, user, _) = await RequireReportAsync(id, ExpenseReportOperation.DeclineLine);
        if (errorResult is not null) return errorResult;

        // Reviewer-only: operator feedback stays in English.
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Reason))
        {
            SetError("A reason is required to decline a line.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await service.DeclineLineWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), lineId, input.Reason);
        SetMutationResult(result, "Line declined.", "Could not decline the line.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Lines/{lineId:guid}/UndoDecline")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoDeclineLine(Guid id, Guid lineId)
    {
        var (errorResult, user, _) = await RequireReportAsync(id, ExpenseReportOperation.DeclineLine);
        if (errorResult is not null) return errorResult;

        var result = await service.UndoDeclineLineWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), lineId);
        SetMutationResult(result, "Decline undone.", "Could not undo the decline.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>
    /// The one review queue, scoped to the viewer (peterdrier/Humans#1447). It lists and links;
    /// every decision is taken from <see cref="Detail"/>.
    /// </summary>
    [HttpGet("Review")]
    public async Task<IActionResult> Review()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            var isFinanceAdmin = await IsFinanceAdminAsync();
            var reports = await service.GetReviewQueueAsync(user.Id, isFinanceAdmin, HttpContext.RequestAborted);
            var submitterNames = await ResolveSubmitterNamesAsync(reports, HttpContext.RequestAborted);
            return View(new ExpenseReviewViewModel
            {
                Reports = reports,
                SubmitterNames = submitterNames,
                BookingLabels = await ResolveBookingLabelsAsync(reports, HttpContext.RequestAborted),
                Grants = await ResolveGrantsAsync(reports, HttpContext.RequestAborted),
                // Finance admins are the only ones who can act on a written-off push, and the
                // list is queue-wide rather than scoped, so it stays with them.
                FailedHoldedPushReportIds = isFinanceAdmin
                    ? (await service.GetFailedHoldedPushReportIdsAsync(HttpContext.RequestAborted)).ToHashSet()
                    : [],
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading expense review queue");
            SetError(localizer["Expenses_Flash_LoadReviewQueueFailed"]);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("{id:guid}/Approve")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> Approve(Guid id, ApproveInputModel input)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Approve);
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            SetError(localizer["Expenses_Flash_InvalidApprovalInput"]);
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await service.ApproveWithResultAsync(
            id, user.Id, input.OverrideAccountNumber, input.MaxAmount);
        SetMutationResult(result, "Report approved.", "Could not approve the report.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Reject")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> Reject(Guid id, FinanceRejectInputModel input)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.FinanceReject);
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Reason))
        {
            SetError(localizer["Expenses_Flash_RejectionReasonRequired"]);
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await service.FinanceRejectWithResultAsync(id, user.Id, input.Reason);
        SetMutationResult(result, "Report rejected.", "Could not reject the report.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/HoldedRetry")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
    public async Task<IActionResult> HoldedRetry(Guid id)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.RequeueHoldedPush);
        if (errorResult is not null) return errorResult;

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
        Guid submitterUserId, bool isFinanceAdmin, CancellationToken ct)
    {
        if (!isFinanceAdmin) return (null, null, false, []);

        var binding = await holdedFinance.GetCreditorContactByUserAsync(submitterUserId, ct);
        var accounts = (await holdedFinance.ListCreditorAccountsAsync(ct)).Accounts
            .OrderBy(a => a.SupplierAccountNum)
            .ToList();

        var boundName = binding?.SupplierAccountNum is { } boundNum
            ? accounts.FirstOrDefault(a => a.SupplierAccountNum == boundNum)?.Name
            : null;

        return (binding?.SupplierAccountNum, boundName, binding is not null, accounts);
    }

    /// <summary>
    /// Which decisions the detail page offers the current user. The authorization handler owns the
    /// actor matrix; the status guard here mirrors the repository's, so a button that would be
    /// refused on arrival is not drawn.
    /// </summary>
    private async Task<(bool Approve, bool FinanceReject, bool Endorse, bool CoordinatorReject)>
        ResolveDecisionsAsync(ExpenseReportDto report)
    {
        var isPending = report.Status is ExpenseReportStatus.Submitted
            or ExpenseReportStatus.CoordinatorEndorsed;
        return (
            Approve: isPending && await AllowsAsync(report, ExpenseReportOperation.Approve),
            FinanceReject: isPending && await AllowsAsync(report, ExpenseReportOperation.FinanceReject),
            // The handler already restricts both coordinator operations to Submitted.
            Endorse: await AllowsAsync(report, ExpenseReportOperation.Endorse),
            CoordinatorReject: await AllowsAsync(report, ExpenseReportOperation.CoordinatorReject));
    }

    /// <summary>Resolve the actor and report before any action-specific work. On failure the
    /// action must return ErrorResult; User and Report are valid only on success, matching
    /// RequireCurrentUserAsync's contract. Special access rules stay with their actions.</summary>
    private async Task<(IActionResult? ErrorResult, UserInfo User, ExpenseReportDto Report)>
        RequireReportAsync(Guid id, ExpenseReportOperation? operation = null, CancellationToken ct = default)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
        if (errorResult is not null) return (errorResult, null!, null!);

        var report = await service.GetAsync(id, ct);
        if (report is null) return (NotFound(), null!, null!);
        if (operation is { } required && !await AllowsAsync(report, required))
            return (Forbid(), null!, null!);

        return (null, user, report);
    }

    private async Task<bool> AllowsAsync(ExpenseReportDto report, ExpenseReportOperation operation) =>
        (await authService.AuthorizeAsync(User, report,
            new ExpenseReportOperationRequirement(operation))).Succeeded;

    /// <summary>
    /// The actor's finance-admin authority, handed to the service so its own ownership and status
    /// checks know an admin is acting for a member (see <c>IExpenseReportManagement</c> remarks).
    /// </summary>
    private async Task<bool> IsFinanceAdminAsync() =>
        (await authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;

    /// <summary>
    /// The line pages stay open to the submitter after submission as a read-only view of their own
    /// receipts, so access is wider than the Edit grant that decides whether the controls render.
    /// </summary>
    private async Task<(bool Allowed, bool CanEditLines)> ResolveLinePageAccessAsync(
        ExpenseReportDto report, Guid userId)
    {
        var canEditLines = await AllowsAsync(report, ExpenseReportOperation.Edit);
        return (canEditLines || report.SubmitterUserId == userId, canEditLines);
    }

    /// <summary>
    /// The submitter keeps this at any status — it is their own profile — on top of whoever the
    /// Edit grant covers.
    /// </summary>
    private async Task<bool> CanSetReportIbanAsync(ExpenseReportDto report, Guid userId) =>
        report.SubmitterUserId == userId || await AllowsAsync(report, ExpenseReportOperation.Edit);

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveSubmitterNamesAsync(
        IReadOnlyCollection<ExpenseReportDto> reports, CancellationToken ct)
    {
        var ids = reports.Select(r => r.SubmitterUserId).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var users = await _userService.GetUserInfosAsync(ids, ct);
        return ids
            .Where(id => users.TryGetValue(id, out var user) && !string.IsNullOrWhiteSpace(user.BurnerName))
            .ToDictionary(id => id, id => users[id].BurnerName);
    }

    /// <summary>Report id → what it books to, as a label: the Holded account's label (retired
    /// accounts included, so an old report still reads), or for reports that predate the account
    /// column, their category in the booked year (active year only for unpinned legacy rows).</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ResolveBookingLabelsAsync(
        IReadOnlyList<ExpenseReportDto> reports, CancellationToken ct)
    {
        var labels = new Dictionary<Guid, string>();
        if (reports.Count == 0) return labels;

        Dictionary<int, string>? accountLabels = null;
        Dictionary<Guid, string>? unpinnedCategoryLabels = null;
        var categoryLabelsByYear = new Dictionary<Guid, Dictionary<Guid, string>>();
        foreach (var report in reports)
        {
            if (report.HoldedAccountNumber != 0)
            {
                accountLabels ??= (await holdedFinance.ListExpenseAccountsAsync(activeOnly: false, ct))
                    .GroupBy(a => a.AccountNum)
                    .ToDictionary(g => g.Key, g => g.First().Label);
                labels[report.Id] = accountLabels.TryGetValue(report.HoldedAccountNumber, out var accountLabel)
                    ? accountLabel
                    : report.HoldedAccountNumber.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var categoryLabels = report.BudgetYearId is { } yearId
                    ? categoryLabelsByYear.GetValueOrDefault(yearId)
                    : unpinnedCategoryLabels;
                if (categoryLabels is null)
                {
                    var year = report.BudgetYearId is { } bookedYearId
                        ? await budgetService.GetYearByIdAsync(bookedYearId, ct)
                        : await budgetService.GetActiveYearAsync(ct);
                    categoryLabels = year?.Groups
                        .SelectMany(g => g.Categories.Select(c => (c.Id, Display: $"{g.Name} / {c.Name}")))
                        .ToDictionary(x => x.Id, x => x.Display)
                        ?? [];
                    if (report.BudgetYearId is { } loadedYearId)
                        categoryLabelsByYear[loadedYearId] = categoryLabels;
                    else
                        unpinnedCategoryLabels = categoryLabels;
                }
                labels[report.Id] = report.BudgetCategoryId is { } categoryId
                                    && categoryLabels.TryGetValue(categoryId, out var categoryLabel)
                    ? categoryLabel
                    : localizer["Expenses_Detail_UnknownCategory"].Value;
            }
        }
        return labels;
    }

    /// <summary>Report id → the pre-approval it files against, for the queue's pre-approval column.</summary>
    private async Task<IReadOnlyDictionary<Guid, GrantOption>> ResolveGrantsAsync(
        IReadOnlyList<ExpenseReportDto> reports, CancellationToken ct)
    {
        var grants = new Dictionary<Guid, GrantOption>();
        foreach (var report in reports)
            if (report.PreapprovalGrantId is { } grantId && await preapprovals.GetGrantAsync(grantId, ct) is { } grant)
                grants[report.Id] = grant;
        return grants;
    }

    private async Task PopulateEditModelAsync(ExpenseEditViewModel model, ExpenseReportDto report)
    {
        model.Report = report;
        model.Accounts = await BuildAccountOptionsAsync();
        // The submitter's grants, not the viewer's: an admin editing on their behalf swaps between
        // the member's own pre-approvals. The one the report holds stays listed in any state.
        var grants = (await preapprovals.ListClaimableForUserAsync(report.SubmitterUserId, HttpContext.RequestAborted)).OrderBy(g => g.ClaimBy).ToList();
        if (report.PreapprovalGrantId is { } held && grants.All(g => g.Id != held)
            && await preapprovals.GetGrantAsync(held, HttpContext.RequestAborted) is { } heldGrant)
            grants.Insert(0, heldGrant);
        model.Grants = grants;
        model.Today = Today;
    }

    /// <summary>The accounts a report may book to: Finance's active expense accounts, budget-category
    /// accounts first. Which year a pending report's category may belong to is the service's call
    /// (it refuses a cross-year rebooking), so one list serves the new form, the edit form and the
    /// approval override.</summary>
    private async Task<IReadOnlyList<HoldedExpenseAccountOption>> BuildAccountOptionsAsync(
        CancellationToken ct = default)
    {
        var accounts = await holdedFinance.ListExpenseAccountsAsync(activeOnly: true, ct);
        return accounts
            .OrderByDescending(a => a.IsBudgetCategory)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? GetErrorMessage(ExpenseError? error)
    {
        if (error is null) return null;
        if (error.OperatorMessage is not null) return error.OperatorMessage;
        var arguments = error.Arguments.Select(argument => argument is ExpenseReportStatus status
            ? localizer.EnumDisplay(status)
            : argument).ToArray();
        return localizer[error.ResourceKey!, arguments].Value;
    }

    private void SetMutationResult(
        ExpenseMutationResult result, string successMessage, string fallbackErrorMessage)
    {
        if (result.Succeeded)
            SetSuccess(successMessage);
        else
            SetError(GetErrorMessage(result.Error) ?? fallbackErrorMessage);
    }

    private void SetMutationResultWithDetails(
        ExpenseMutationResult result, string successMessage, string errorPrefix)
    {
        if (result.Succeeded)
            SetSuccess(successMessage);
        else
            SetError(GetErrorMessage(result.Error) is null ? errorPrefix : $"{errorPrefix}: {GetErrorMessage(result.Error)}");
    }

    private async Task<(bool HasIban, string? MaskedIban)> GetIbanViewAsync(Guid userId, CancellationToken ct = default)
    {
        var iban = (await _userService.GetUserInfoAsync(userId, ct))?.Profile?.Iban;
        var hasIban = !string.IsNullOrEmpty(iban);
        return (hasIban, hasIban ? IbanFormatter.Mask(iban!) : null);
    }

}
