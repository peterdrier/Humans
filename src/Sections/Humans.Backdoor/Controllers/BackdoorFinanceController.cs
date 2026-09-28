using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Backdoor.Filters;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// Read-only Backdoor export of expense reports, creditor bindings, the category map and SEPA
/// transfers, for the agent doing the books who needs Humans' side of a Holded break
/// (peterdrier/Humans#1838). Same pattern as <see cref="BackdoorStoreController"/>
/// (peterdrier/Humans#1719): every action reuses an <c>*ServiceRead</c> method, sorts and formats.
/// </summary>
/// <remarks>
/// Unlike Store, a key here does not read everything its owner's role permits without a check —
/// authorization is imperative, the same way <c>ExpensesController</c> does it: each action calls
/// <see cref="IAuthorizationService"/> against the key owner's roles, which
/// <see cref="BackdoorApiKeyAuthFilter"/> has already installed on <c>User</c>. GET only, nothing
/// here writes to the DB or to Holded, and no raw IBAN ever leaves this controller —
/// <see cref="ExpenseReportDto.PayeeIban"/> and <see cref="HoldedContactInfo.Iban"/> are masked
/// in the projections, and <see cref="OnResultExecuting"/> runs every other string any action
/// emits through <see cref="IbanFormatter.MaskAllIn"/> on the way out.
/// </remarks>
[ApiController]
[Route("api/backdoor/finance")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
internal sealed class BackdoorFinanceController(
    IExpenseReportServiceRead expenses,
    IHoldedFinanceServiceRead finance,
    IBudgetServiceRead budget,
    IAuthorizationService authService,
    IUserServiceRead users) : ApiControllerBase(users), IResultFilter
{
    // ─── Expense reports ────────────────────────────────────────────────────────

    /// <summary>The key owner's review queue — everything <c>/Expenses/Review</c> would show them —
    /// optionally narrowed to one budget year and/or status.</summary>
    [HttpGet("expense-reports")]
    public async Task<IActionResult> ExpenseReports(
        [FromQuery] string? year, [FromQuery] string? status, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Unauthorized();

        var isFinanceAdmin = await IsFinanceAdminAsync();
        var reports = await expenses.GetReviewQueueAsync(userId, isFinanceAdmin, ct);
        var years = await ResolveBudgetYearsAsync(reports, ct);
        var names = await ResolveNamesAsync(reports.Select(r => r.SubmitterUserId).ToList(), ct);

        var filtered = reports
            .Where(r => string.IsNullOrEmpty(year)
                || string.Equals(years.GetValueOrDefault(r.BudgetYearId)?.Year, year, StringComparison.OrdinalIgnoreCase))
            .Where(r => string.IsNullOrEmpty(status)
                || string.Equals(r.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.SubmittedAt ?? r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToList();

        var rows = new List<object>(filtered.Count);
        foreach (var r in filtered)
        {
            var isSubmitter = r.SubmitterUserId == userId;
            // The list only carries push state, which the browser shows finance admins only
            // (ExpensesController.Detail) — nothing to fetch for a submitter who isn't one.
            var timeline = isFinanceAdmin ? await expenses.GetHoldedTimelineAsync(r, ct) : null;
            years.TryGetValue(r.BudgetYearId, out var yearDetail);
            rows.Add(ProjectReportSummary(
                r, yearDetail, names.GetValueOrDefault(r.SubmitterUserId), timeline, isSubmitter, isFinanceAdmin));
        }

        return Ok(rows);
    }

    /// <summary>One report in full, gated by the same <c>View</c> check the browser detail page
    /// uses — a report outside what the key owner may view is a 403, not a 404, so the caller can
    /// tell "denied" from "no such report".</summary>
    [HttpGet("expense-reports/{id:guid}")]
    public async Task<IActionResult> ExpenseReport(Guid id, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } userId) return Unauthorized();

        var report = await expenses.GetAsync(id, ct);
        if (report is null) return NotFound();
        if (!await CanViewAsync(report)) return StatusCode(StatusCodes.Status403Forbidden);

        var years = await ResolveBudgetYearsAsync([report], ct);
        years.TryGetValue(report.BudgetYearId, out var yearDetail);
        var submitter = await FindUserInfoByIdAsync(report.SubmitterUserId, ct);

        var isSubmitter = report.SubmitterUserId == userId;
        var isFinanceAdmin = await IsFinanceAdminAsync();
        // The submitter reads the payment half of the timeline; the finance admin reads the push
        // half — same split as ExpensesController.Detail. A viewer who is neither (e.g. a category
        // coordinator, whose View passes on a different ground) gets neither.
        var timeline = isSubmitter || isFinanceAdmin ? await expenses.GetHoldedTimelineAsync(report, ct) : null;

        return Ok(ProjectReportDetail(report, yearDetail, submitter?.BurnerName, timeline, isSubmitter, isFinanceAdmin));
    }

    /// <summary>The stored bytes for one attachment, gated by the owning report's <c>View</c>
    /// check.</summary>
    [HttpGet("expense-reports/{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Attachment(Guid id, Guid attachmentId, CancellationToken ct)
    {
        if (GetCurrentUserId() is null) return Unauthorized();

        var owningReport = await expenses.GetReportOwningAttachmentAsync(attachmentId, ct);
        if (owningReport is null || owningReport.Id != id) return NotFound();
        if (!await CanViewAsync(owningReport)) return StatusCode(StatusCodes.Status403Forbidden);

        var attachment = await expenses.TryReadAttachmentAsync(owningReport, attachmentId, ct);
        if (attachment is null) return NotFound();

        return File(attachment.Bytes, attachment.ContentType, attachment.OriginalFileName);
    }

    // ─── Creditor accounts ──────────────────────────────────────────────────────

    /// <summary>Every 400000xx account, its bindings, and the bindings with no account at all.</summary>
    [HttpGet("creditor-accounts")]
    public async Task<IActionResult> CreditorAccounts(CancellationToken ct)
    {
        if (GetCurrentUserId() is null) return Unauthorized();
        if (!await IsFinanceAdminAsync()) return StatusCode(StatusCodes.Status403Forbidden);

        var (accounts, unresolved) = await finance.ListCreditorAccountsAsync(ct);
        var names = await ResolveNamesAsync(
            accounts.SelectMany(a => a.Bindings).Select(b => b.UserId)
                .Concat(unresolved.Select(b => b.UserId))
                .ToList(), ct);

        return Ok(new
        {
            accounts = accounts.Select(a => ProjectCreditorAccount(a, names)),
            unresolved = unresolved.Select(b => ProjectBinding(b, names)),
        });
    }

    /// <summary>One account's balance and every cached journal line, contact header included.</summary>
    [HttpGet("creditor-accounts/{num:int}/ledger")]
    public async Task<IActionResult> CreditorLedger(int num, CancellationToken ct)
    {
        if (GetCurrentUserId() is null) return Unauthorized();
        if (!await IsFinanceAdminAsync()) return StatusCode(StatusCodes.Status403Forbidden);

        var ledger = await finance.GetCreditorLedgerAsync(num, ct);
        if (ledger is null) return NotFound();

        return Ok(new
        {
            supplierAccountNum = ledger.SupplierAccountNum,
            balance = ledger.Balance,
            owedToMember = ledger.OwedToMember,
            lines = ledger.Lines.Select(ProjectLedgerLine),
            contact = ProjectContact(ledger.Contact),
        });
    }

    // ─── Category map & SEPA ────────────────────────────────────────────────────

    /// <summary>Every live category-map row — what each budget category is actually booked to.</summary>
    [HttpGet("category-map")]
    public async Task<IActionResult> CategoryMap(CancellationToken ct)
    {
        if (GetCurrentUserId() is null) return Unauthorized();
        if (!await IsFinanceAdminAsync()) return StatusCode(StatusCodes.Status403Forbidden);

        var rows = await finance.GetCategoryMapAsync(ct);
        return Ok(rows.Select(m => new
        {
            budgetCategoryId = m.BudgetCategoryId,
            categoryName = m.CategoryName,
            holdedAccountNumber = m.HoldedAccountNumber,
            holdedAccountId = m.HoldedAccountId,
            tag = m.Tag,
            isActive = m.IsActive,
        }));
    }

    /// <summary>Every generated SEPA transfer with its booking state.</summary>
    [HttpGet("sepa-transfers")]
    public async Task<IActionResult> SepaTransfers(CancellationToken ct)
    {
        if (GetCurrentUserId() is null) return Unauthorized();
        if (!await IsFinanceAdminAsync()) return StatusCode(StatusCodes.Status403Forbidden);

        var (transfers, unavailableReason) = await finance.GetSepaTransfersAsync(ct);
        var names = await ResolveNamesAsync(
            transfers.SelectMany(t => new[] { t.UserId, t.GeneratedByUserId })
                .Concat(transfers.Where(t => t.BookedByUserId is not null).Select(t => t.BookedByUserId!.Value))
                .ToList(), ct);

        return Ok(new
        {
            unavailableReason,
            transfers = transfers.Select(t => ProjectSepaTransfer(t, names)),
        });
    }

    // ─── Holded sync ────────────────────────────────────────────────────────────

    /// <summary>The purchase-doc sync's state plus the docs it could not match.</summary>
    [HttpGet("holded-sync")]
    public async Task<IActionResult> HoldedSync(CancellationToken ct)
    {
        if (GetCurrentUserId() is null) return Unauthorized();
        if (!await IsFinanceAdminAsync()) return StatusCode(StatusCodes.Status403Forbidden);

        var syncInfo = await finance.GetDocSyncInfoAsync(ct);
        var unmatched = await finance.GetUnmatchedAsync(ct);

        return Ok(new
        {
            lastSyncAt = syncInfo.LastSyncAt?.ToIso8601(),
            status = syncInfo.Status,
            lastError = syncInfo.LastError,
            lastSyncedDocCount = syncInfo.LastSyncedDocCount,
            creditorBindingCount = syncInfo.CreditorBindingCount,
            unmatched = unmatched.Select(u => new
            {
                holdedDocId = u.HoldedDocId,
                docNumber = u.DocNumber,
                contactName = u.ContactName,
                description = u.Description,
                total = u.Total,
                reason = u.Reason,
                holdedUrl = u.HoldedUrl,
            }),
        });
    }

    // ─── IBAN scrub ─────────────────────────────────────────────────────────────

    /// <summary>The one place free text is scrubbed: every string in every JSON body, and the
    /// attachment's download filename, goes through <see cref="IbanFormatter.MaskAllIn"/> after
    /// the action, so a field added later cannot forget to (peterdrier/Humans#1839). File bytes
    /// are left alone.</summary>
    public void OnResultExecuting(ResultExecutingContext context)
    {
        switch (context.Result)
        {
            case ObjectResult { Value: { } value } body:
                var options = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
                var node = JsonSerializer.SerializeToNode(value, value.GetType(), options);
                MaskIbans(node);
                body.Value = node;
                break;
            case FileContentResult file:
                file.FileDownloadName = IbanFormatter.MaskAllIn(file.FileDownloadName);
                break;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static void MaskIbans(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj.Select(p => p.Value).ToList()) MaskIbans(child);
                break;
            case JsonArray array:
                foreach (var child in array.ToList()) MaskIbans(child);
                break;
            // A Guid is never an IBAN, but a dashed one can pass as a spaced one by checksum luck.
            case JsonValue value when value.GetValueKind() == JsonValueKind.String
                && value.GetValue<string>() is var text && !Guid.TryParse(text, out _):
                var masked = IbanFormatter.MaskAllIn(text);
                if (!string.Equals(masked, text, StringComparison.Ordinal)) value.ReplaceWith(masked);
                break;
        }
    }

    // ─── Shared authorization ───────────────────────────────────────────────────

    private async Task<bool> IsFinanceAdminAsync() =>
        (await authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;

    private async Task<bool> CanViewAsync(ExpenseReportDto report) =>
        (await authService.AuthorizeAsync(User, report, PolicyNames.ExpenseReportView)).Succeeded;

    // ─── Shared lookups ─────────────────────────────────────────────────────────

    /// <summary>The full budget-year projection for every distinct year the given reports book to —
    /// gives both the year label and, per report, its category's name in one lookup (a report can be
    /// booked to a year other than the active one).</summary>
    private async Task<Dictionary<Guid, BudgetYearDetail>> ResolveBudgetYearsAsync(
        IReadOnlyCollection<ExpenseReportDto> reports, CancellationToken ct)
    {
        var result = new Dictionary<Guid, BudgetYearDetail>();
        foreach (var yearId in reports.Select(r => r.BudgetYearId).Distinct())
        {
            var year = await budget.GetYearByIdAsync(yearId);
            if (year is not null) result[yearId] = year;
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var distinct = userIds.Distinct().ToList();
        if (distinct.Count == 0) return new Dictionary<Guid, string>();

        var infos = await UserService.GetUserInfosAsync(distinct, ct);
        return distinct.ToDictionary(
            id => id,
            id => infos.TryGetValue(id, out var u) && !string.IsNullOrWhiteSpace(u.BurnerName)
                ? u.BurnerName
                : "(unknown)");
    }

    private static string? CategoryName(BudgetYearDetail? year, Guid categoryId) =>
        year?.Groups.SelectMany(g => g.Categories).FirstOrDefault(c => c.Id == categoryId)?.Name;

    // ─── Projections ────────────────────────────────────────────────────────────

    /// <summary>The payee name, the masked IBAN, and the Holded timeline are only ever meaningful to
    /// the report's submitter or a finance admin — the same audience <c>ExpensesController.Detail</c>
    /// shows them to (peterdrier/Humans#1839, fixing M1). The Holded contact/supplier/doc ids are
    /// finance-admin only, matching the same view's finance card. Everyone else who can pass the
    /// <c>View</c> check (e.g. a category coordinator) gets these fields null.</summary>
    private static object ProjectReportSummary(
        ExpenseReportDto r, BudgetYearDetail? year, string? submitterName, ExpenseHoldedTimeline? timeline,
        bool isSubmitter, bool isFinanceAdmin)
    {
        var showPayee = isSubmitter || isFinanceAdmin;
        // Push half — finance-admin only. Nulled here rather than per-field below, so the
        // projection itself stays a single flat set of member accesses (HUM0031).
        var push = isFinanceAdmin ? timeline : null;
        return new
        {
            id = r.Id,
            status = r.Status.ToString(),
            note = r.Note,
            submitterUserId = r.SubmitterUserId,
            submitterName = submitterName ?? "(unknown)",
            payeeName = showPayee ? r.PayeeName : null,
            payeeIbanMasked = showPayee ? IbanFormatter.Mask(r.PayeeIban) : null,
            budgetCategoryId = r.BudgetCategoryId,
            budgetCategoryName = CategoryName(year, r.BudgetCategoryId),
            budgetYear = year?.Year,
            total = r.Total,
            maxAmount = r.MaxAmount,
            payable = r.Payable,
            submittedAt = r.SubmittedAt?.ToIso8601(),
            coordinatorEndorsedAt = r.CoordinatorEndorsedAt?.ToIso8601(),
            coordinatorEndorsedByUserId = r.CoordinatorEndorsedByUserId,
            approvedAt = r.ApprovedAt?.ToIso8601(),
            approvedByUserId = r.ApprovedByUserId,
            lastRejectedAt = r.LastRejectedAt?.ToIso8601(),
            lastRejectedByUserId = r.LastRejectedByUserId,
            lastRejectionReason = r.LastRejectionReason,
            holdedContactId = isFinanceAdmin ? r.HoldedContactId : null,
            holdedSupplierAccountNum = isFinanceAdmin ? r.HoldedSupplierAccountNum : null,
            holdedDocIds = isFinanceAdmin ? r.HoldedDocIds : null,
            syncState = push?.SyncState.ToString(),
            queuedAt = push?.QueuedAt?.ToIso8601(),
            settledAt = push?.SettledAt?.ToIso8601(),
            retryCount = push?.RetryCount,
            maxRetries = push?.MaxRetries,
            lastError = push?.LastError,
            nextRetryAt = push?.NextRetryAt?.ToIso8601(),
        };
    }

    /// <summary>Same submitter/finance-admin split as <see cref="ProjectReportSummary"/>, plus the
    /// detail-only payment half — submitter-only, mirroring <c>ExpensesController.Detail</c>'s
    /// "Payment status" card.</summary>
    private static object ProjectReportDetail(
        ExpenseReportDto r, BudgetYearDetail? year, string? submitterName, ExpenseHoldedTimeline? timeline,
        bool isSubmitter, bool isFinanceAdmin)
    {
        var showPayee = isSubmitter || isFinanceAdmin;
        var push = isFinanceAdmin ? timeline : null;
        var payment = isSubmitter ? timeline : null;
        return new
        {
            id = r.Id,
            status = r.Status.ToString(),
            note = r.Note,
            submitterUserId = r.SubmitterUserId,
            submitterName = submitterName ?? "(unknown)",
            payeeName = showPayee ? r.PayeeName : null,
            payeeIbanMasked = showPayee ? IbanFormatter.Mask(r.PayeeIban) : null,
            budgetCategoryId = r.BudgetCategoryId,
            budgetCategoryName = CategoryName(year, r.BudgetCategoryId),
            budgetYear = year?.Year,
            total = r.Total,
            maxAmount = r.MaxAmount,
            payable = r.Payable,
            submittedAt = r.SubmittedAt?.ToIso8601(),
            coordinatorEndorsedAt = r.CoordinatorEndorsedAt?.ToIso8601(),
            coordinatorEndorsedByUserId = r.CoordinatorEndorsedByUserId,
            approvedAt = r.ApprovedAt?.ToIso8601(),
            approvedByUserId = r.ApprovedByUserId,
            lastRejectedAt = r.LastRejectedAt?.ToIso8601(),
            lastRejectedByUserId = r.LastRejectedByUserId,
            lastRejectionReason = r.LastRejectionReason,
            holdedContactId = isFinanceAdmin ? r.HoldedContactId : null,
            holdedSupplierAccountNum = isFinanceAdmin ? r.HoldedSupplierAccountNum : null,
            holdedDocIds = isFinanceAdmin ? r.HoldedDocIds : null,
            syncState = push?.SyncState.ToString(),
            queuedAt = push?.QueuedAt?.ToIso8601(),
            settledAt = push?.SettledAt?.ToIso8601(),
            retryCount = push?.RetryCount,
            maxRetries = push?.MaxRetries,
            lastError = push?.LastError,
            nextRetryAt = push?.NextRetryAt?.ToIso8601(),
            registeredInHolded = payment?.RegisteredInHolded,
            owedToMember = payment?.OwedToMember,
            memberRegisteredTotal = payment?.MemberRegisteredTotal,
            otherAmount = payment?.OtherAmount,
            paid = payment?.Paid,
            paidOn = payment?.PaidOn?.ToInvariantDate(),
            totalPaid = payment?.TotalPaid,
            lines = r.Lines.OrderBy(l => l.SortOrder).Select(l => ProjectLine(l, isFinanceAdmin)),
        };
    }

    private static object ProjectLine(ExpenseLineDto l, bool isFinanceAdmin) => new
    {
        id = l.Id,
        description = l.Description,
        amount = l.Amount,
        lineType = l.LineType.ToString(),
        parentLineId = l.ParentLineId,
        sortOrder = l.SortOrder,
        holdedDocId = isFinanceAdmin ? l.HoldedDocId : null,
        attachment = l.Attachment is { } a ? new
        {
            id = a.Id,
            fileName = a.OriginalFileName,
            contentType = a.ContentType,
            sizeBytes = a.SizeBytes,
        } : null,
    };

    private static object ProjectBinding(CreditorContactBinding b, IReadOnlyDictionary<Guid, string> names) => new
    {
        userId = b.UserId,
        userName = names.GetValueOrDefault(b.UserId, b.UserId.ToString()),
        holdedContactId = b.HoldedContactId,
        source = b.Source.ToString(),
    };

    private static object ProjectCreditorAccount(
        HoldedCreditorAccountRow a, IReadOnlyDictionary<Guid, string> names) => new
        {
            supplierAccountNum = a.SupplierAccountNum,
            name = a.Name,
            balance = a.Balance,
            owedToMember = a.OwedToMember,
            ibanMasked = a.IbanMasked,
            bindings = a.Bindings.Select(b => ProjectBinding(b, names)),
        };

    private static object ProjectLedgerLine(CreditorLedgerLine l) => new
    {
        entryNumber = l.EntryNumber,
        line = l.Line,
        date = l.Date.ToIso8601(),
        accountNum = l.AccountNum,
        debit = l.Debit,
        credit = l.Credit,
        type = l.Type,
        description = l.Description,
    };

    /// <summary>Never null-forgiving on <see cref="HoldedContactInfo.Iban"/> — masked here, the one
    /// place this DTO crosses into Backdoor's output.</summary>
    private static object? ProjectContact(HoldedContactInfo? c) => c is null ? null : new
    {
        name = c.Name,
        tradeName = c.TradeName,
        email = c.Email,
        phone = c.Phone,
        mobile = c.Mobile,
        ibanMasked = IbanFormatter.Mask(c.Iban),
        taxCode = c.TaxCode,
        address = c.Address,
    };

    private static object ProjectSepaTransfer(
        SepaPayoutTransferRow t, IReadOnlyDictionary<Guid, string> names) => new
        {
            transferId = t.TransferId,
            fileId = t.FileId,
            fileName = t.FileName,
            generatedAt = t.GeneratedAt.ToIso8601(),
            generatedBy = names.GetValueOrDefault(t.GeneratedByUserId, t.GeneratedByUserId.ToString()),
            userId = t.UserId,
            memberName = names.GetValueOrDefault(t.UserId, t.UserId.ToString()),
            supplierAccountNum = t.SupplierAccountNum,
            holdedContactId = t.HoldedContactId,
            creditorName = t.CreditorName,
            ibanMasked = t.IbanMasked,
            amount = t.Amount,
            bookedAt = t.BookedAt?.ToIso8601(),
            bookedBy = t.BookedByUserId is { } id ? names.GetValueOrDefault(id, id.ToString()) : null,
            holdedBankMovementId = t.HoldedBankMovementId,
            reconciledAt = t.ReconciledAt?.ToIso8601(),
            reconcilePending = t.ReconcilePending,
            notBookableReason = t.NotBookableReason,
        };
}
