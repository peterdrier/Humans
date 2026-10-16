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
[CrossSectionWrite("Publishes expense documents and creditor bindings through Finance and Holded contracts.")]
internal sealed class ExpenseHoldedPublisher(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IUserServiceRead userService,
    IHoldedClient holdedClient,
    IHoldedFinanceService holdedFinance,
    IAuditLogService auditLogService,
    ILogger logger) : IApplicationService
{
    [ExternalWrite]
    public async Task PublishAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string submitterName,
        Instant now,
        CancellationToken ct)
    {
        // 1. Ensure the member's Holded creditor contact + binding (Finance owns creditor identity).
        //    Reuses the binding — including an admin's manual bind — or lazy-seeds from a cached
        //    contact id; never mints a duplicate. Legal name -> name; burner -> tradeName.
        //    The creditor is the member as they are now: a report submitted under a since-merged
        //    id binds (and seeds from) the survivor, so one human keeps one Holded contact.
        var submitter = await userService.GetUserInfoAsync(report.SubmitterUserId, ct);
        var creditorUserId = submitter?.Id ?? report.SubmitterUserId;
        var burnerName = string.IsNullOrWhiteSpace(report.PayeeName) ? null : submitter?.BurnerName;

        // This report carries a contact id only on a re-drain. Seeding from it alone misses a member
        // whose contact predates holded_creditor_contacts (that migration creates the table and
        // backfills nothing), leaving them with a contact on older reports and no binding row — and a
        // null seed makes Finance POST a *second* Holded contact, splitting their payables across two.
        // Lazy-seed from their most recent linked report instead; the push then writes the binding.
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

        var holdedContactId = await holdedFinance.EnsureCreditorContactAsync(
            creditorUserId, report.PayeeName, burnerName, report.PayeeIban,
            seedContactId, seedAccountNum, ct);

        // Mirror the contact id onto the report (keeps the creditor-timeline reads working) before the
        // retryable doc-create + attachment steps. The supplier-account number is backfilled in step 4.
        await repo.SetHoldedContactLinkAsync(report.Id, holdedContactId, null, now, ct);

        // Books items[].account directly at doc creation — the account IS the category. Null when
        // the category has no active mapping; the doc still creates, just unbooked.
        var holdedAccountId = await holdedFinance.GetHoldedAccountIdForCategoryAsync(report.BudgetCategoryId, ct);

        // 2–4. One purchase doc per bookable line, each carrying its one receipt. A report pushed
        // before per-line docs carries a report-level doc id instead: that push resumes onto its
        // single doc rather than minting per-line duplicates next to it.
        string pushedSummary;
        if (!string.IsNullOrEmpty(report.HoldedDocId))
        {
            await ResumeLegacySingleDocPushAsync(report, report.HoldedDocId, now, ct);
            pushedSummary = $"Pushed to Holded as purchase document {report.HoldedDocId}.";
        }
        else
        {
            pushedSummary = await PushPerLineDocsAsync(
                report, submitterName, holdedContactId, holdedAccountId, now, ct);
        }

        // 5. Resolve supplierRecord.num (now that a payable exists) and persist the contact link.
        // Best-effort: the doc is already created, so a failure here must NOT fail the outbox event
        // (that would strand a created doc as permanently-failed). There is no automatic retry — a null
        // num stays null until an admin runs POST /Finance/Creditors/Bind, or a later report for this
        // same member resolves it (nobodies-collective/Humans#972). ListCreditorAccountsAsync returns
        // such bindings in its Unresolved half, which is what makes the gap visible on
        // /Finance/Creditors so that manual step is discoverable.
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
            await holdedFinance.SetCreditorAccountNumAsync(creditorUserId, supplierAccountNum.Value, ct);

        await repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);

        await auditLogService.LogAsync(
            AuditAction.ExpenseHoldedPushed,
            AuditEntityTypes.Report, report.Id,
            pushedSummary,
            ExpenseReportService.OutboxJobName);
    }

    /// <summary>
    /// One purchase doc per bookable line, in <c>SortOrder</c>: create (idempotent on the line's
    /// <c>HoldedDocId</c>), upload its one receipt (idempotent on <c>HoldedUploadedAt</c>), approve
    /// (idempotent on the doc's <c>ApprovedAt</c> — POST /purchases only creates a draft, and an
    /// unapproved doc never books to the ledger). A line the cap trims books its receipt at face
    /// value plus a negative adjustment line on the same account; a line the cap zeroed out gets no
    /// doc and no upload — the skip is named in the returned audit summary, since Holded then holds
    /// no record of that receipt at all.
    /// </summary>
    private async Task<string> PushPerLineDocsAsync(
        ExpenseReportDto report,
        string submitterName,
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
                        ContactName = submitterName,
                        Date = report.SubmittedAt ?? report.CreatedAt,
                        // The report's Note stays in Humans — the doc's breadcrumb back to its
                        // report is what the accountant needs, not member↔reviewer context. It
                        // goes in Notes so the ledger description (Holded-rendered) stays readable.
                        Description = $"ER: {line.Description}",
                        Notes = $"Expense report {report.Id}",
                        Lines = docLines,
                    }, ct);
                await repo.SetLineHoldedDocIdAsync(line.Id, holdedDocId, now, ct);
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
        return full.Length <= maxSummaryLength ? full : full[..(maxSummaryLength - 1)] + "…";
    }

    /// <summary>
    /// Finishes a push that started before per-line docs: the report-level doc already exists in
    /// Holded (possibly with some receipts on it), so upload what is still missing and approve —
    /// never create a second payable next to it.
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

    /// <summary>
    /// Uploads the line's receipt to the given doc. Each upload is recorded so a re-run — after a
    /// failure partway through the push, or after a finance admin requeues the event — resumes
    /// instead of adding a second copy of every earlier file.
    /// </summary>
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
