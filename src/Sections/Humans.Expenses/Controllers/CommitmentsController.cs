using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Budget.Contracts;
using Humans.Expenses.Models;
using Humans.Expenses.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Expenses.Controllers;

/// <summary>
/// The vendor commitment registry (nobodies-collective/Humans#1030) — record a quote before any
/// money moves, record payments against it, and match the real invoice back from Holded.
/// Finance-admin only: these are the organisation's liabilities, not any member's expenses.
/// </summary>
[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
[Route("Expenses/Commitments")]
internal sealed class CommitmentsController(
    IUserServiceRead userService,
    IVendorCommitmentService service,
    IBudgetServiceRead budgetService,
    IClock clock,
    ILogger<CommitmentsController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var commitments = await service.ListAsync(HttpContext.RequestAborted);
        return View(new CommitmentIndexViewModel
        {
            Commitments = commitments,
            CategoryNames = await CategoryNamesAsync(),
            HoldedConfigured = service.MatchingAvailable,
        });
    }

    [HttpGet("AwaitingInvoice")]
    public async Task<IActionResult> AwaitingInvoice()
    {
        var commitments = await service.ListPaidAwaitingInvoiceAsync(HttpContext.RequestAborted);
        return View(new CommitmentAwaitingInvoiceViewModel
        {
            Commitments = commitments,
            CategoryNames = await CategoryNamesAsync(),
            Now = clock.GetCurrentInstant(),
        });
    }

    [HttpGet("New")]
    public async Task<IActionResult> New() =>
        View(new CommitmentNewViewModel { Categories = await BuildCategoryOptionsAsync() });

    [HttpPost("New")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(CommitmentNewViewModel model, IFormFile? quote)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            model.Categories = await BuildCategoryOptionsAsync();
            return View(model);
        }

        // The stream is opened here and closed here; the service only reads it.
        await using var content = quote is { Length: > 0 } ? quote.OpenReadStream() : null;
        var upload = content is null
            ? null
            : new ExpenseFileUpload(quote!.FileName, quote.ContentType, content);

        var (result, commitmentId) = await service.CreateAsync(
            model.VendorName, model.ExpectedAmount, model.Purpose,
            model.BudgetCategoryId, user.Id, upload, HttpContext.RequestAborted);

        if (commitmentId is not { } id)
        {
            SetError(result.ErrorMessage ?? "Could not record the commitment.");
            model.Categories = await BuildCategoryOptionsAsync();
            return View(model);
        }

        // An id with a failed result means the commitment exists but a follow-up did not: send the
        // operator to it carrying the warning, never back to a form they would submit again.
        if (result.Succeeded) SetSuccess("Commitment recorded.");
        else SetError(result.ErrorMessage ?? "The commitment was recorded, but a follow-up failed.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var commitment = await service.GetAsync(id, HttpContext.RequestAborted);
        if (commitment is null) return NotFound();

        var names = await CategoryNamesAsync();
        return View(new CommitmentDetailViewModel
        {
            Commitment = commitment,
            CategoryDisplayName = commitment.BudgetCategoryId is { } cid
                && names.TryGetValue(cid, out var display) ? display : null,
        });
    }

    [HttpPost("{id:guid}/Payments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordPayment(Guid id, RecordCommitmentPaymentInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            SetError("Enter a payment amount and the date it left the account.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        var result = await service.RecordPaymentAsync(
            id, input.Amount, LocalDate.FromDateOnly(input.PaidOn), input.Reference,
            user.Id, HttpContext.RequestAborted);

        if (result.Succeeded) SetSuccess("Payment recorded.");
        else SetError(result.ErrorMessage ?? "Could not record the payment.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:guid}/Quote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadQuote(Guid id, IFormFile? quote)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (quote is not { Length: > 0 })
        {
            SetError("Please select a file.");
            return RedirectToAction(nameof(Detail), new { id });
        }

        await using var content = quote.OpenReadStream();
        var result = await service.AttachQuoteAsync(
            id, user.Id, new ExpenseFileUpload(quote.FileName, quote.ContentType, content),
            HttpContext.RequestAborted);

        if (result.Succeeded) SetSuccess("Quote attached.");
        else SetError(result.ErrorMessage ?? "Could not attach the quote.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:guid}/Quote")]
    public async Task<IActionResult> DownloadQuote(Guid id)
    {
        var file = await service.GetQuoteFileAsync(id, HttpContext.RequestAborted);
        return file is not { } f ? NotFound() : File(f.Content, f.ContentType, f.FileName);
    }

    [HttpPost("{id:guid}/Close")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var result = await service.CloseAsync(id, user.Id, HttpContext.RequestAborted);
        if (result.Succeeded) SetSuccess("Commitment closed.");
        else SetError(result.ErrorMessage ?? "Could not close the commitment.");

        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("Match")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Match()
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var (result, run) = await service.RunMatchingAsync(user.Id, HttpContext.RequestAborted);
        if (!result.Succeeded || run is null)
        {
            SetError(result.ErrorMessage ?? "Matching could not run.");
            return RedirectToAction(nameof(Index));
        }

        logger.LogInformation(
            "Commitment matching examined {Examined}, linked {Linked}, queued {Ambiguous} ambiguous and {Duplicates} duplicates",
            run.Examined, run.Linked, run.Ambiguous, run.Duplicates);

        SetSuccess(
            $"Checked {run.Examined} commitment(s): {run.Linked} linked, " +
            $"{run.Ambiguous} ambiguous and {run.Duplicates} suspected duplicate(s) sent for review.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Candidates/{candidateId:guid}/Resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveCandidate(Guid candidateId, bool accepted, Guid commitmentId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var result = await service.ResolveCandidateAsync(
            candidateId, accepted, user.Id, HttpContext.RequestAborted);

        if (result.Succeeded) SetSuccess(accepted ? "Document linked." : "Review item dismissed.");
        else SetError(result.ErrorMessage ?? "Could not resolve the review item.");

        return RedirectToAction(nameof(Detail), new { id = commitmentId });
    }

    private async Task<IReadOnlyList<BudgetCategoryOption>> BuildCategoryOptionsAsync()
    {
        var activeYear = await budgetService.GetActiveYearAsync();
        return activeYear?.Groups
            .SelectMany(g => g.Categories.Select(c => new BudgetCategoryOption(c.Id, g.Name, c.Name)))
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCulture)
            .ToList()
            ?? [];
    }

    private async Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync()
    {
        var options = await BuildCategoryOptionsAsync();
        return options.ToDictionary(o => o.Id, o => o.DisplayName);
    }
}
