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
    IFinanceService finance,
    IPreapprovalService preapprovals,
    IAuthorizationService authService,
    IClock clock,
    ILogger<ExpensesController> logger,
    IStringLocalizer<ExpensesResource> localizer) : HumansControllerBase(userService)
{
    private readonly ExpensePageBuilder pageBuilder = new(service, budgetService, finance, preapprovals, clock, localizer);
    private readonly IUserService _userService = userService;

    private LocalDate Today => pageBuilder.Today;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            var model = await pageBuilder.BuildIndexAsync(user.Id, HttpContext.RequestAborted);
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

    /// <summary><paramref name="grant"/> preselects that pre-approval. Any grant is accepted here so a finance admin can
    /// file on a member's behalf; the service only lets the submitter's own be claimed.</summary>
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
        var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        // Blank means "me": the picker renders only for finance admins, and clearing its search clears the hidden id.
        var submitterUserId = model.SubmitterUserId is { } picked && picked != Guid.Empty
            ? picked
            : user.Id;
        if (submitterUserId != user.Id && !await IsFinanceAdminAsync()) return Forbid();

        // A pre-approval brings its own account, so the account field is not required with one.
        if (model.PreapprovalGrantId is not null) ModelState.Remove(nameof(model.HoldedAccountNumber));

        if (!ModelState.IsValid)
        {
            model.Accounts = await BuildAccountOptionsAsync(HttpContext.RequestAborted);
            await PopulateGrantOptionsAsync(model, user.Id, HttpContext.RequestAborted);
            return View(model);
        }

        try
        {
            var result = await service.CreateDraftAsync(
                submitterUserId, user.Id, model.HoldedAccountNumber, model.Note, model.PreapprovalGrantId);
            if (result.Succeeded)
            {
                SetSuccess(localizer["Expenses_Flash_DraftCreated"]);
                return RedirectToAction(nameof(Edit), new { id = result.ReportId });
            }

            if (result.ReportId is { } savedId)
            {
                SetError(localizer["Expenses_Flash_DraftSavedFollowupFailed"]);
                return RedirectToAction(nameof(Edit), new { id = savedId });
            }
            if (result.OutcomeUncertain)
            {
                SetError(localizer["Expenses_Flash_DraftOutcomeUnknown"]);
                return RedirectToAction(nameof(Index));
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

    /// <summary>The viewer's claimable grants, plus the preselected one when it is somebody else's (an admin filing on
    /// their behalf).</summary>
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
            var isSubmitter = user.AllUserIds.Contains(report.SubmitterUserId);
            var canEdit = await AllowsAsync(report, ExpenseReportOperation.Edit);
            // Always the report's IBAN state, never the viewer's: an admin setting it up needs the member's answer.
            var iban = isSubmitter || canEdit
                ? await GetIbanViewAsync(report.SubmitterUserId, HttpContext.RequestAborted)
                : (HasIban: false, MaskedIban: null);

            var isFinanceAdmin = await IsFinanceAdminAsync();
            var decisions = await ResolveDecisionsAsync(report);
            // Anyone who may view the report sees its whole Holded timeline; nothing in it is sensitive.
            var timeline = await service.GetHoldedTimelineAsync(report, HttpContext.RequestAborted);
            var creditor = await GetCreditorBindingViewAsync(report.SubmitterUserId, isFinanceAdmin, HttpContext.RequestAborted);
            var grant = report.PreapprovalGrantId is { } grantId
                ? await preapprovals.GetGrantAsync(grantId, HttpContext.RequestAborted)
                : null;

            // Reaching this page needs View, which only the submitter, the first reviewer and finance admins
            // hold, so the viewer may see who submitted. The IBAN stays behind CanSeePayee.
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
                CreditorAccountsUnavailable = creditor.Unavailable,
                CanApprove = decisions.Approve,
                CanFinanceReject = decisions.FinanceReject,
                CanEndorse = decisions.Endorse,
                CanCoordinatorReject = decisions.CoordinatorReject,
                CanDeclineLines = await AllowsAsync(report, ExpenseReportOperation.DeclineLine),
                // Only the approval form offers an account override.
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
                // A viewer who may read but not change the report is told why; anyone else is refused.
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
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit, HttpContext.RequestAborted);
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
    [RequestSizeLimit(25 * 1024 * 1024)] // the service enforces 20 MB and the content type
    public async Task<IActionResult> AddLine(Guid id, AddLineInputModel input, IFormFile? file)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit, HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        // Where the submitter came from, and returns to on a validation error.
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
        var (allowed, canEditLines) = await ResolveLinePageAccessAsync(report, user);
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
        var (allowed, canEditLines) = await ResolveLinePageAccessAsync(report, user);
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
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit, HttpContext.RequestAborted);
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
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit, HttpContext.RequestAborted);
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
    [RequestSizeLimit(25 * 1024 * 1024)] // the service enforces 20 MB and the content type
    public async Task<IActionResult> AttachFile(Guid id, Guid lineId, IFormFile? file)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit, HttpContext.RequestAborted);
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
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Edit, HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        var result = await service.RemoveAttachmentFromLineAsync(id, user.Id, await IsFinanceAdminAsync(), lineId);
        SetMutationResult(result, localizer["Expenses_Flash_AttachmentRemoved"], localizer["Expenses_Flash_RemoveAttachmentFailed"]);
        return RedirectToAction(nameof(LineEdit), new { id, lineId });
    }

    [HttpPost("{id:guid}/Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid id)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Submit, HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        var result = await service.SubmitWithResultAsync(id, user.Id, await IsFinanceAdminAsync());
        SetMutationResult(result, localizer["Expenses_Flash_ReportSubmitted"], localizer["Expenses_Flash_SubmitFailed"]);

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Withdraw, HttpContext.RequestAborted);
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
            if (!await CanSetReportIbanAsync(report, user)) return Forbid();

            // Always the submitter's IBAN: this page sets who gets paid, which is never the viewer when an admin
            // opens it.
            var iban = await GetIbanViewAsync(report.SubmitterUserId, HttpContext.RequestAborted);
            var model = new ExpenseIbanViewModel
            {
                ReportId = id,
                HasIban = iban.HasIban,
                MaskedIban = iban.MaskedIban,
                ReportStatus = report.Status,
                OnBehalfOfUserId = user.AllUserIds.Contains(report.SubmitterUserId) ? null : report.SubmitterUserId,
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
        var (errorResult, user, report) = await RequireReportAsync(id, ct: HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;
        if (!await CanSetReportIbanAsync(report, user)) return Forbid();

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
        model.OnBehalfOfUserId = user.AllUserIds.Contains(report.SubmitterUserId) ? null : report.SubmitterUserId;
        return View(model);
    }

    [HttpGet("Attachment/{attachmentId:guid}")]
    public Task<IActionResult> Attachment(Guid attachmentId)
        => StreamAttachmentAsync(attachmentId, inline: false);

    /// <summary>Rendered in the tab (images and PDFs) so a reviewer can check receipts without saving them. Other types
    /// fall back to download.</summary>
    [HttpGet("Attachment/{attachmentId:guid}/View")]
    public Task<IActionResult> AttachmentView(Guid attachmentId)
        => StreamAttachmentAsync(attachmentId, inline: true);

    private async Task<IActionResult> StreamAttachmentAsync(Guid attachmentId, bool inline)
    {
        try
        {
            var (errorResult, _) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            // NotFound for both a miss and a denial, so nothing leaks.
            var owningReport = await service.GetReportOwningAttachmentAsync(attachmentId, HttpContext.RequestAborted);
            if (owningReport is null) return NotFound();

            var authResult = await authService.AuthorizeAsync(User, owningReport,
                new ExpenseReportOperationRequirement(ExpenseReportOperation.View));
            if (!authResult.Succeeded) return NotFound();

            var attachment = await service.TryReadAttachmentAsync(owningReport, attachmentId, HttpContext.RequestAborted);
            if (attachment is null) return NotFound();

            // No filename means no Content-Disposition: attachment, so the browser shows it in the tab.
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
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Endorse, HttpContext.RequestAborted);
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
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.CoordinatorReject, HttpContext.RequestAborted);
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
        var (errorResult, user, _) = await RequireReportAsync(id, ExpenseReportOperation.DeclineLine, HttpContext.RequestAborted);
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
        var (errorResult, user, _) = await RequireReportAsync(id, ExpenseReportOperation.DeclineLine, HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        var result = await service.UndoDeclineLineWithResultAsync(
            id, user.Id, await IsFinanceAdminAsync(), lineId);
        SetMutationResult(result, "Decline undone.", "Could not undo the decline.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>It lists and links; every decision is taken from <see cref="Detail"/>.</summary>
    [HttpGet("Review")]
    public async Task<IActionResult> Review()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
            if (errorResult is not null) return errorResult;

            var isFinanceAdmin = await IsFinanceAdminAsync();
            return View(await pageBuilder.BuildReviewAsync(user.Id, isFinanceAdmin, HttpContext.RequestAborted));
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
    [Authorize(Policy = FinancePolicies.Manage)]
    public async Task<IActionResult> Approve(Guid id, ApproveInputModel input)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.Approve, HttpContext.RequestAborted);
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
    [Authorize(Policy = FinancePolicies.Manage)]
    public async Task<IActionResult> Reject(Guid id, FinanceRejectInputModel input)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.FinanceReject, HttpContext.RequestAborted);
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
    [Authorize(Policy = FinancePolicies.Manage)]
    public async Task<IActionResult> HoldedRetry(Guid id)
    {
        var (errorResult, user, report) = await RequireReportAsync(id, ExpenseReportOperation.RequeueHoldedPush, HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        var result = await service.RequeueHoldedPushWithResultAsync(id, user.Id);
        SetMutationResult(result,
            "Holded push re-queued — it runs on the next drain pass.",
            "Could not re-queue the Holded push.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>
    /// Lets a finance admin bind the submitter before approval, so the push reuses the right 400000xx instead of
    /// minting a duplicate. Empty for everyone else.
    /// </summary>
    private async Task<(int? BoundAccountNum, string? BoundAccountName, bool HasContact,
        IReadOnlyList<CreditorAccountRow> Accounts, string? Unavailable)> GetCreditorBindingViewAsync(
        Guid submitterUserId, bool isFinanceAdmin, CancellationToken ct)
    {
        if (!isFinanceAdmin) return (null, null, false, [], null);

        var binding = await finance.GetCreditorContactByUserAsync(submitterUserId, ct);
        var (rows, _, unavailable) = await finance.ListCreditorAccountsAsync(ct);
        var accounts = rows.OrderBy(a => a.SupplierAccountNum).ToList();

        // The row Finance placed the binding on, through Holded's contact: the binding's stored number can be stale.
        var bound = accounts.FirstOrDefault(a => a.Bindings.Any(b => b.UserId == submitterUserId));
        return (bound?.SupplierAccountNum, bound?.Name, binding is not null, accounts, unavailable);
    }

    /// <summary>The authorization handler owns the actor matrix and each decision's status gate.</summary>
    private async Task<(bool Approve, bool FinanceReject, bool Endorse, bool CoordinatorReject)>
        ResolveDecisionsAsync(ExpenseReportDto report)
    {
        return (
            Approve: await AllowsAsync(report, ExpenseReportOperation.Approve),
            FinanceReject: await AllowsAsync(report, ExpenseReportOperation.FinanceReject),
            Endorse: await AllowsAsync(report, ExpenseReportOperation.Endorse),
            CoordinatorReject: await AllowsAsync(report, ExpenseReportOperation.CoordinatorReject));
    }

    /// <summary>On failure the action must return ErrorResult; User and Report are valid only on success.</summary>
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
    /// Handed to the service so its ownership and status checks know an admin is acting for a member.
    /// </summary>
    private async Task<bool> IsFinanceAdminAsync() =>
        (await authService.AuthorizeAsync(User, FinancePolicies.Manage)).Succeeded;

    /// <summary>
    /// The line pages stay open to the submitter after submission as a read-only view of their receipts, so access is
    /// wider than the Edit grant that decides whether the controls render.
    /// </summary>
    private async Task<(bool Allowed, bool CanEditLines)> ResolveLinePageAccessAsync(
        ExpenseReportDto report, UserInfo user)
    {
        var canEditLines = await AllowsAsync(report, ExpenseReportOperation.Edit);
        return (canEditLines || user.AllUserIds.Contains(report.SubmitterUserId), canEditLines);
    }

    /// <summary>The submitter keeps this at any status (it is their own profile), on top of the Edit grant.</summary>
    private async Task<bool> CanSetReportIbanAsync(ExpenseReportDto report, UserInfo user) =>
        user.AllUserIds.Contains(report.SubmitterUserId) || await AllowsAsync(report, ExpenseReportOperation.Edit);

    /// <summary>The account's label (retired accounts included, so an old report still reads), or for reports that
    /// predate the account column, their category in the booked year.</summary>
    private Task<IReadOnlyDictionary<Guid, string>> ResolveBookingLabelsAsync(
        IReadOnlyList<ExpenseReportDto> reports, CancellationToken ct) =>
        pageBuilder.ResolveBookingLabelsAsync(reports, ct);

    private async Task PopulateEditModelAsync(ExpenseEditViewModel model, ExpenseReportDto report)
    {
        model.Report = report;
        model.Accounts = await BuildAccountOptionsAsync(HttpContext.RequestAborted);
        // The submitter's grants, not the viewer's: an admin editing on their behalf swaps between the member's own.
        // The one the report holds stays listed in any state.
        var grants = (await preapprovals.ListClaimableForUserAsync(report.SubmitterUserId, HttpContext.RequestAborted)).OrderBy(g => g.ClaimBy).ToList();
        if (report.PreapprovalGrantId is { } held && grants.All(g => g.Id != held)
            && await preapprovals.GetGrantAsync(held, HttpContext.RequestAborted) is { } heldGrant)
            grants.Insert(0, heldGrant);
        model.Grants = grants;
        model.Today = Today;
    }

    /// <summary>Budget-category accounts first. The service refuses a cross-year rebooking, so one list serves the new
    /// form, the edit form and the approval override.</summary>
    private async Task<IReadOnlyList<ExpenseAccountOption>> BuildAccountOptionsAsync(
        CancellationToken ct = default)
    {
        var accounts = await finance.ListExpenseAccountsAsync(activeOnly: true, ct);
        return accounts
            .OrderByDescending(a => a.IsBudgetCategory)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.AccountNum)
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

    private Task<(bool HasIban, string? MaskedIban)> GetIbanViewAsync(Guid userId, CancellationToken ct = default) =>
        pageBuilder.GetIbanViewAsync(userId, ct);

}
