using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Expenses.Services;

/// <summary>The resumable Holded document and attachment conversation for an approved expense report.</summary>
internal sealed class ExpenseHoldedPublisher(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IUserService userService,
    IHoldedClient holdedClient,
    IHoldedFinanceService holdedFinance,
    IHoldedFinanceWrite holdedFinanceWrite,
    IAuditLogService auditLogService,
    ILogger logger) : IApplicationService
{
    [ExternalWrite]
    public async Task PublishAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        Instant now,
        CancellationToken ct)
    {
        // 1. Ensure the member's Holded creditor contact and binding (Finance owns creditor identity). Reuses the
        //    binding, including an admin's manual bind, or seeds from a cached contact id; never mints a duplicate.
        //    The creditor is the member as they are now: a since-merged id binds the survivor.
        var submitter = await userService.GetUserInfoAsync(report.SubmitterUserId, ct);
        var creditorUserId = submitter?.Id ?? report.SubmitterUserId;
        var burnerName = string.IsNullOrWhiteSpace(report.PayeeName) ? null : submitter?.BurnerName;

        // This report carries a contact id only on a re-drain. A member whose contact predates
        // holded_creditor_contacts has no binding row, and a null seed makes Finance POST a second Holded contact,
        // splitting their payables. So seed from their most recent linked report.
        var seedContactId = report.HoldedContactId;
        var seedAccountNum = report.HoldedSupplierAccountNum;
        if (string.IsNullOrEmpty(seedContactId))
        {
            var priorReports = new List<ExpenseReportDto>();
            foreach (var submitterId in submitter?.AllUserIds ?? [report.SubmitterUserId])
                priorReports.AddRange(await repo.GetForSubmitterAsync(submitterId, ct));
            var priorLinked = priorReports
                .Where(r => r.Id != report.Id && !string.IsNullOrEmpty(r.HoldedContactId))
                .OrderByDescending(r => r.SubmittedAt ?? r.CreatedAt)
                .FirstOrDefault();
            seedContactId = priorLinked?.HoldedContactId;
            seedAccountNum = priorLinked?.HoldedSupplierAccountNum;
        }

        var holdedContactId = await holdedFinanceWrite.EnsureCreditorContactAsync(
            creditorUserId, report.PayeeName, burnerName, report.PayeeIban,
            seedContactId, seedAccountNum, ExpenseReportBackgroundProcessor.OutboxJobName, ct);

        // Mirror the contact id onto the report (the creditor timeline reads it) before the retryable steps.
        await repo.SetHoldedContactLinkAsync(report.Id, holdedContactId, null, now, ct);

        // Reports that predate the account column resolve through their category's mapping; null when there is none,
        // and the doc still creates, just unbooked.
        var holdedAccountId = report.HoldedAccountId
            ?? (report.BudgetCategoryId is { } categoryId
                ? await holdedFinance.GetHoldedAccountIdForCategoryAsync(categoryId, ct)
                : null);

        // 2–4. One purchase doc per bookable line. A report pushed before per-line docs resumes onto its single doc
        // rather than minting per-line duplicates next to it.
        string pushedSummary;
        if (!string.IsNullOrEmpty(report.HoldedDocId))
        {
            await ResumeLegacySingleDocPushAsync(report, report.HoldedDocId, now, ct);
            pushedSummary = $"Pushed to Holded as purchase document {report.HoldedDocId}.";
        }
        else
        {
            pushedSummary = await PushPerLineDocsAsync(
                report, holdedContactId, holdedAccountId, now, ct);
        }

        // 5. Resolve supplierRecord.num now that a payable exists. Best-effort: failing the event here would strand a
        // created doc as permanently failed. Nothing retries; a null num stays until an admin binds it on
        // /Finance/Creditors (where ListCreditorAccountsAsync's Unresolved half shows it) or a later report resolves it.
        int? supplierAccountNum = null;
        try
        {
            var contact = await holdedClient.GetContactAsync(holdedContactId, ct);
            supplierAccountNum = contact.SupplierAccountNum;
        }
        catch (HoldedTransientException ex)
        {
            logger.LogWarning(
                "Could not resolve supplier account number for contact {ContactId}: {Error} — no automatic " +
                "retry; bind manually via POST /Finance/Creditors/Bind if it does not resolve on a later push",
                holdedContactId, ex.Message);
        }
        catch (HoldedPermanentException ex)
        {
            logger.LogWarning(
                "Permanent error resolving supplier account number for contact {ContactId}: {Error} — no " +
                "automatic retry; bind manually via POST /Finance/Creditors/Bind",
                holdedContactId, ex.Message);
        }
        await repo.SetHoldedContactLinkAsync(report.Id, holdedContactId, supplierAccountNum, now, ct);
        if (supplierAccountNum is not null)
            await holdedFinanceWrite.SetCreditorAccountNumAsync(
                creditorUserId, supplierAccountNum.Value, ExpenseReportBackgroundProcessor.OutboxJobName, ct);

        await repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseHoldedPushed,
            AuditEntityTypes.Report, report.Id,
            pushedSummary,
            ExpenseReportBackgroundProcessor.OutboxJobName);
    }

    /// <summary>What a per-line push writes as the document's description; Finance matches on it.</summary>
    internal static string DocDescription(string lineDescription) => $"ER: {lineDescription}";

    /// <summary>What a push writes as the document's notes; Finance matches on it.</summary>
    internal static string DocNotes(Guid reportId) => $"Expense report {reportId}";

    /// <summary>
    /// Each step is idempotent: create on the line's <c>HoldedDocId</c>, upload on <c>HoldedUploadedAt</c>, approve on
    /// the doc's <c>ApprovedAt</c> (POST /purchases only creates a draft, and an unapproved doc never books). A line the
    /// cap zeroed out gets no doc, so the audit summary names the skip: Holded has no record of that receipt.
    /// </summary>
    private async Task<string> PushPerLineDocsAsync(
        ExpenseReportDto report,
        string holdedContactId,
        string? holdedAccountId,
        Instant now,
        CancellationToken ct)
    {
        var allocations = PayableAllocation.Allocate(report);

        var pushedDocIds = new List<string>();
        var notes = new List<string>();
        foreach (var allocation in allocations)
        {
            var line = allocation.Line;
            if (allocation.Booked <= 0m)
            {
                if (allocation.Skipped)
                    notes.Add(
                        $"'{line.Description}' ({Euro(line.Amount)}) not booked — authorized maximum reached.");
                continue;
            }

            var holdedDocId = line.HoldedDocId;
            if (string.IsNullOrEmpty(holdedDocId))
            {
                var docLines = new List<HoldedPurchaseDocumentLineInput>
                {
                    new()
                    {
                        Description = line.Description,
                        Amount = line.Amount,
                        AccountId = holdedAccountId,
                    },
                };
                // The receipt books at face value so the doc matches its attachment; the negative
                // line brings the doc down to what the cap lets this line book.
                if (allocation.Trimmed)
                {
                    docLines.Add(new HoldedPurchaseDocumentLineInput
                    {
                        Description = $"Authorized maximum {Euro(report.Payable)} — adjustment",
                        Amount = allocation.Booked - line.Amount,
                        AccountId = holdedAccountId,
                    });
                }

                holdedDocId = await holdedClient.CreatePurchaseDocumentAsync(
                    new HoldedPurchaseDocumentInput
                    {
                        ContactId = holdedContactId,
                        ContactName = string.IsNullOrWhiteSpace(report.PayeeName) ? "Unknown" : report.PayeeName,
                        Date = report.SubmittedAt ?? report.CreatedAt,
                        // The report's Note stays in Humans: the accountant needs the breadcrumb to the report, which
                        // goes in Notes so the Holded-rendered ledger description stays readable.
                        Description = DocDescription(line.Description),
                        Notes = DocNotes(report.Id),
                        Lines = docLines,
                    }, ct);
                if (!await repo.SetLineHoldedDocIdAsync(line.Id, holdedDocId, now, ct))
                {
                    // Lost a race: another push already recorded a document for this line. Carry on with the stored
                    // one and name ours in the audit summary so it gets removed in Holded by hand.
                    var created = holdedDocId;
                    var stored = (await repo.GetByIdAsync(report.Id, ct))
                        ?.Lines.FirstOrDefault(l => l.Id == line.Id)?.HoldedDocId;
                    if (string.IsNullOrEmpty(stored))
                        throw new InvalidOperationException(
                            $"Line {line.Id} lost the doc-id race but has no stored Holded document id.");
                    logger.LogError(
                        "Expense line {LineId} lost the Holded doc-id race: this run created purchase document " +
                        "{CreatedDocId} but {StoredDocId} was already stored — the orphan needs manual removal in Holded",
                        line.Id, created, stored);
                    notes.Add(
                        $"'{line.Description}': orphan purchase document {created} created by a concurrent push needs manual removal in Holded — {stored} already recorded.");
                    holdedDocId = stored;
                }
            }
            pushedDocIds.Add(holdedDocId);

            if (allocation.Trimmed)
                notes.Add(
                    $"'{line.Description}' booked {Euro(allocation.Booked)} of {Euro(line.Amount)} (authorized maximum).");

            await UploadLineAttachmentAsync(line, holdedDocId, now, ct);

            var currentDoc = await holdedClient.GetPurchaseDocumentAsync(holdedDocId, ct);
            if (currentDoc.ApprovedAt is null)
                await holdedClient.ApprovePurchaseDocumentAsync(holdedDocId, ct);
        }

        var summary = pushedDocIds.Count switch
        {
            0 => "No purchase documents pushed to Holded.",
            1 => $"Pushed to Holded as purchase document {pushedDocIds[0]}.",
            _ => $"Pushed to Holded as purchase documents {string.Join(", ", pushedDocIds)}.",
        };
        var full = notes.Count == 0 ? summary : $"{summary} {string.Join(" ", notes)}";
        // AuditLogEntry.Description caps at 4,000 chars (job-name prefix included) and a failed
        // insert is swallowed, so an oversized summary loses the audit entry entirely.
        const int maxSummaryLength = 3500;
        if (full.Length <= maxSummaryLength)
            return full;

        var length = maxSummaryLength - 1;
        if (char.IsHighSurrogate(full[length - 1]) && char.IsLowSurrogate(full[length]))
            length--;
        return full[..length] + "…";
    }

    /// <summary>
    /// For a push that started before per-line docs: upload what is still missing to the existing doc and approve.
    /// Never create a second payable next to it.
    /// </summary>
    private async Task ResumeLegacySingleDocPushAsync(
        ExpenseReportDto report, string holdedDocId, Instant now, CancellationToken ct)
    {
        foreach (var line in report.Lines
                     .Where(l => l.ParentLineId is null)
                     .OrderBy(l => l.SortOrder))
        {
            await UploadLineAttachmentAsync(line, holdedDocId, now, ct);
        }

        var currentDoc = await holdedClient.GetPurchaseDocumentAsync(holdedDocId, ct);
        if (currentDoc.ApprovedAt is null)
            await holdedClient.ApprovePurchaseDocumentAsync(holdedDocId, ct);
    }

    /// <summary>Each upload is recorded so a re-run resumes instead of adding a second copy of earlier files.</summary>
    private async Task UploadLineAttachmentAsync(
        ExpenseLineDto line, string holdedDocId, Instant now, CancellationToken ct)
    {
        if (line.AttachmentId is null || line.Attachment is null) return;
        if (line.Attachment.HoldedUploadedAt is not null) return;

        var bytes = await fileStorage.TryReadAsync(
            ExpenseReportService.AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
        if (bytes is null)
            throw new InvalidOperationException(
                $"Attachment file for {line.Attachment.Id}{line.Attachment.Extension} could not be read from storage.");
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
        await repo.MarkAttachmentPushedAsync(line.Attachment.Id, now, ct);
    }

    private static string Euro(decimal amount) =>
        $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";

}
