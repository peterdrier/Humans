using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services.Dtos;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Expenses.Services;

/// <summary>
/// Vendor commitments: record before paying, pay against the record, match the real invoice back
/// (nobodies-collective/Humans#1030).
/// </summary>
internal sealed class VendorCommitmentService(
    IVendorCommitmentRepository repo,
    IFileStorage fileStorage,
    IHoldedClient holdedClient,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<VendorCommitmentService> logger) : IVendorCommitmentService
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private const long QuoteMaxBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf", "image/jpeg", "image/jpg", "image/png", "image/heic"
        };

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".jpg", ".jpeg", ".png", ".heic" };

    /// <summary>One quote file per commitment, keyed by the commitment id.</summary>
    internal static string QuoteKey(Guid commitmentId, string extension) =>
        $"uploads/vendor-commitment-quotes/{commitmentId}{extension}";

    public Task<VendorCommitmentDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        repo.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<VendorCommitmentDto>> ListAsync(CancellationToken ct = default) =>
        repo.GetAllAsync(ct);

    public async Task<IReadOnlyList<VendorCommitmentDto>> ListPaidAwaitingInvoiceAsync(
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var all = await repo.GetAllAsync(ct);
        return all
            .Where(c => c.IsPaidAwaitingInvoice)
            .OrderByDescending(c => LiabilityWeight(c, now))
            .ThenByDescending(c => c.TotalPaid)
            .ToList();
    }

    /// <summary>
    /// Age × euros outstanding — the sort the liability list is defined by. Age counts from the
    /// first payment out, since that is when the association started being owed an invoice. Days
    /// are offset by one so the amount still discriminates on the day a payment is made; a bare
    /// multiply would score every same-day liability zero, tiny and six-figure alike.
    /// </summary>
    private static decimal LiabilityWeight(VendorCommitmentDto c, Instant now)
    {
        var oldestPayment = c.Payments.Count == 0 ? c.CreatedAt : c.Payments.Min(p => p.CreatedAt);
        var days = (decimal)Math.Max(0d, (now - oldestPayment).TotalDays);
        return (days + 1m) * c.TotalPaid;
    }

    public async Task<(ExpenseMutationResult Result, Guid? CommitmentId)> CreateAsync(
        string vendorName, decimal expectedAmount, string purpose,
        Guid? budgetCategoryId, string? holdedContactId,
        Guid actorUserId, ExpenseFileUpload? quote = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vendorName))
            return (ExpenseMutationResult.Failure("Vendor name is required."), null);
        if (string.IsNullOrWhiteSpace(purpose))
            return (ExpenseMutationResult.Failure("Purpose is required."), null);
        if (expectedAmount <= 0m)
            return (ExpenseMutationResult.Failure("Expected amount must be greater than zero."), null);
        if (quote is not null && Validate(quote) is { } quoteError)
            return (ExpenseMutationResult.Failure(quoteError), null);

        var now = clock.GetCurrentInstant();
        var commitment = new VendorCommitment
        {
            Id = Guid.NewGuid(),
            VendorName = vendorName.Trim(),
            HoldedContactId = string.IsNullOrWhiteSpace(holdedContactId) ? null : holdedContactId.Trim(),
            ExpectedAmount = expectedAmount,
            Currency = "EUR",
            Purpose = purpose.Trim(),
            BudgetCategoryId = budgetCategoryId,
            Status = VendorCommitmentStatus.Open,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await repo.AddAsync(commitment, ct);

            if (quote is not null)
                await StoreQuoteAsync(commitment.Id, quote, now, ct);

            await auditLogService.LogAsync(
                AuditAction.VendorCommitmentRecorded,
                AuditEntityTypes.Commitment, commitment.Id,
                $"Commitment to {commitment.VendorName} for " +
                $"{expectedAmount.ToString("0.00", CultureInfo.InvariantCulture)} EUR recorded.",
                actorUserId);

            return (ExpenseMutationResult.Success, commitment.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error recording vendor commitment for {Vendor}", vendorName);
            return (ExpenseMutationResult.Failure(ex.Message), null);
        }
    }

    public async Task<ExpenseMutationResult> AttachQuoteAsync(
        Guid commitmentId, Guid actorUserId, ExpenseFileUpload quote, CancellationToken ct = default)
    {
        if (Validate(quote) is { } error) return ExpenseMutationResult.Failure(error);

        var commitment = await repo.GetByIdAsync(commitmentId, ct);
        if (commitment is null) return ExpenseMutationResult.Failure("Commitment not found.");
        if (commitment.QuoteFileName is not null)
            return ExpenseMutationResult.Failure("This commitment already has a quote attached.");

        try
        {
            await StoreQuoteAsync(commitmentId, quote, clock.GetCurrentInstant(), ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error attaching quote to commitment {CommitmentId}", commitmentId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    public async Task<ExpenseMutationResult> RecordPaymentAsync(
        Guid commitmentId, decimal amount, LocalDate paidOn, string? reference,
        Guid actorUserId, CancellationToken ct = default)
    {
        if (amount <= 0m)
            return ExpenseMutationResult.Failure("Payment amount must be greater than zero.");

        var commitment = await repo.GetByIdAsync(commitmentId, ct);
        if (commitment is null) return ExpenseMutationResult.Failure("Commitment not found.");
        if (commitment.Status == VendorCommitmentStatus.Closed)
            return ExpenseMutationResult.Failure("This commitment is closed; no further payments can be recorded.");

        var now = clock.GetCurrentInstant();
        var payment = new VendorCommitmentPayment
        {
            Id = Guid.NewGuid(),
            Amount = amount,
            PaidOn = paidOn,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            RecordedByUserId = actorUserId,
            CreatedAt = now,
        };

        var newStatus = NextPaymentStatus(commitment, commitment.TotalPaid + amount);

        try
        {
            if (!await repo.AddPaymentAsync(commitmentId, payment, newStatus, now, ct))
                return ExpenseMutationResult.Failure("Commitment not found.");

            await auditLogService.LogAsync(
                AuditAction.VendorCommitmentPaymentRecorded,
                AuditEntityTypes.Commitment, commitmentId,
                $"Payment of {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR " +
                $"recorded against commitment to {commitment.VendorName}.",
                actorUserId);

            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error recording payment on commitment {CommitmentId}", commitmentId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Payment status is derived, never chosen: Open → PartiallyPaid → Paid on the sum of the
    /// rows. An already-Invoiced commitment keeps that status — receiving the invoice is further
    /// along the lifecycle than paying, so a late payment must not walk it backwards.
    /// </summary>
    private static VendorCommitmentStatus NextPaymentStatus(
        VendorCommitmentDto commitment, decimal totalPaidAfter) =>
        commitment.Status == VendorCommitmentStatus.Invoiced
            ? VendorCommitmentStatus.Invoiced
            : totalPaidAfter >= commitment.ExpectedAmount
                ? VendorCommitmentStatus.Paid
                : VendorCommitmentStatus.PartiallyPaid;

    public async Task<ExpenseMutationResult> CloseAsync(
        Guid commitmentId, Guid actorUserId, CancellationToken ct = default)
    {
        var commitment = await repo.GetByIdAsync(commitmentId, ct);
        if (commitment is null) return ExpenseMutationResult.Failure("Commitment not found.");

        // Two ways out of the registry: the invoice arrived and the cost is booked, or the quote
        // was never taken up and no money moved. Anything else still owes someone an invoice.
        var closable = commitment.Status == VendorCommitmentStatus.Invoiced
            || (commitment.Status == VendorCommitmentStatus.Open && commitment.TotalPaid == 0m);
        if (!closable)
            return ExpenseMutationResult.Failure(
                "Only an invoiced commitment, or an unpaid one being abandoned, can be closed.");

        if (!await repo.CloseAsync(commitmentId, clock.GetCurrentInstant(), ct))
            return ExpenseMutationResult.Failure("Commitment not found.");

        await auditLogService.LogAsync(
            AuditAction.VendorCommitmentClosed,
            AuditEntityTypes.Commitment, commitmentId,
            $"Commitment to {commitment.VendorName} closed.",
            actorUserId);

        return ExpenseMutationResult.Success;
    }

    public async Task<(ExpenseMutationResult Result, VendorCommitmentMatchRunResult? Run)> RunMatchingAsync(
        Guid actorUserId, CancellationToken ct = default)
    {
        if (!holdedClient.IsConfigured)
            return (ExpenseMutationResult.Failure("Holded is not configured in this environment."), null);

        IReadOnlyList<HoldedPurchaseDocListItemDto> docs;
        try
        {
            docs = await holdedClient.ListPurchaseDocumentsAsync(ct);
        }
        catch (HoldedApiException ex)
        {
            logger.LogError(ex, "Could not list Holded purchase documents for commitment matching");
            return (ExpenseMutationResult.Failure($"Holded rejected the request: {ex.Message}"), null);
        }

        var commitments = await repo.GetAllAsync(ct);

        // A document already linked to one commitment can never be the invoice for another.
        var claimed = commitments
            .Select(c => c.MatchedHoldedDocId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var now = clock.GetCurrentInstant();
        int linked = 0, ambiguous = 0, duplicates = 0, examined = 0;

        foreach (var commitment in commitments)
        {
            if (commitment.Status == VendorCommitmentStatus.Closed) continue;
            examined++;

            // `claimed` holds this commitment's own document too, so an invoiced commitment sees
            // only the *other* documents that fit — which is exactly the dupe population.
            var pool = docs
                .Where(d => !claimed.Contains(d.Id))
                .Select(d => new MatchableDocument(
                    d.Id, d.DocNumber, d.ContactName, d.Date.InZone(MadridZone).Date, d.Total))
                .ToList();

            var outcome = VendorCommitmentMatcher.Match(
                commitment.ExpectedAmount, commitment.VendorName,
                commitment.MatchedHoldedDocId is not null, pool);

            switch (outcome.Decision)
            {
                case VendorCommitmentMatchDecision.Link when outcome.Linked is { } doc:
                    if (await repo.LinkPurchaseDocumentAsync(
                            commitment.Id, doc.Id, doc.DocNumber, now, ct))
                    {
                        claimed.Add(doc.Id);
                        linked++;
                        await auditLogService.LogAsync(
                            AuditAction.VendorCommitmentInvoiceLinked,
                            AuditEntityTypes.Commitment, commitment.Id,
                            $"Purchase document {doc.DocNumber} linked to commitment to " +
                            $"{commitment.VendorName}.",
                            actorUserId);
                    }
                    await repo.ReplacePendingCandidatesAsync(commitment.Id, [], ct);
                    break;

                case VendorCommitmentMatchDecision.Review:
                    await repo.ReplacePendingCandidatesAsync(
                        commitment.Id,
                        [.. outcome.ForReview.Select(d => ToCandidate(d, outcome.ReviewKind, now))],
                        ct);
                    if (outcome.ReviewKind == VendorCommitmentMatchKind.Duplicate)
                    {
                        duplicates += outcome.ForReview.Count;
                        await auditLogService.LogAsync(
                            AuditAction.VendorCommitmentDuplicateFlagged,
                            AuditEntityTypes.Commitment, commitment.Id,
                            $"{outcome.ForReview.Count} purchase document(s) match the already-invoiced " +
                            $"commitment to {commitment.VendorName} and were flagged, not linked.",
                            actorUserId);
                    }
                    else
                    {
                        ambiguous += outcome.ForReview.Count;
                    }
                    break;

                case VendorCommitmentMatchDecision.NoMatch:
                default:
                    await repo.ReplacePendingCandidatesAsync(commitment.Id, [], ct);
                    break;
            }
        }

        return (ExpenseMutationResult.Success,
            new VendorCommitmentMatchRunResult(examined, linked, ambiguous, duplicates));
    }

    private static VendorCommitmentMatchCandidate ToCandidate(
        MatchableDocument d, VendorCommitmentMatchKind kind, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            HoldedDocId = d.Id,
            HoldedDocNumber = d.DocNumber,
            ContactName = d.ContactName,
            DocDate = d.Date,
            DocTotal = d.Total,
            Kind = kind,
            DetectedAt = now,
        };

    public async Task<ExpenseMutationResult> ResolveCandidateAsync(
        Guid candidateId, bool accepted, Guid actorUserId, CancellationToken ct = default)
    {
        var candidate = await repo.GetCandidateAsync(candidateId, ct);
        if (candidate is null) return ExpenseMutationResult.Failure("Review item not found.");
        if (candidate.ResolvedAt is not null)
            return ExpenseMutationResult.Failure("This review item has already been resolved.");

        var now = clock.GetCurrentInstant();
        var commitmentId = await repo.ResolveCandidateAsync(candidateId, accepted, actorUserId, now, ct);
        if (commitmentId is not { } id) return ExpenseMutationResult.Failure("Review item not found.");

        if (!accepted) return ExpenseMutationResult.Success;

        if (!await repo.LinkPurchaseDocumentAsync(
                id, candidate.HoldedDocId, candidate.HoldedDocNumber, now, ct))
            return ExpenseMutationResult.Failure(
                "This commitment already carries a purchase document. Dismiss the item instead, " +
                "or unlink the existing document in Holded first.");

        await auditLogService.LogAsync(
            AuditAction.VendorCommitmentInvoiceLinked,
            AuditEntityTypes.Commitment, id,
            $"Purchase document {candidate.HoldedDocNumber} linked after review.",
            actorUserId);

        return ExpenseMutationResult.Success;
    }

    public async Task<(byte[] Content, string ContentType, string FileName)?> GetQuoteFileAsync(
        Guid commitmentId, CancellationToken ct = default)
    {
        var commitment = await repo.GetByIdAsync(commitmentId, ct);
        if (commitment is null) return null;
        if (commitment is not
            { QuoteFileName: { } name, QuoteContentType: { } type, QuoteExtension: { } ext })
            return null;

        var bytes = await fileStorage.TryReadAsync(QuoteKey(commitmentId, ext), ct);
        return bytes is null ? null : (bytes, type, name);
    }

    private async Task StoreQuoteAsync(
        Guid commitmentId, ExpenseFileUpload quote, Instant now, CancellationToken ct)
    {
        var extension = Path.GetExtension(quote.FileName).ToLowerInvariant();
        await fileStorage.SaveAsync(QuoteKey(commitmentId, extension), quote.Content, ct);
        await repo.SetQuoteAsync(
            commitmentId, Path.GetFileName(quote.FileName), quote.ContentType, extension, now, ct);
    }

    /// <summary>Null when the upload is acceptable, otherwise the message to show.</summary>
    private static string? Validate(ExpenseFileUpload quote)
    {
        if (quote.Content.Length == 0) return "Please select a file.";
        if (quote.Content.Length > QuoteMaxBytes)
            return $"File too large. Maximum size is {QuoteMaxBytes / (1024 * 1024)} MB.";

        var extension = Path.GetExtension(quote.FileName).ToLowerInvariant();
        return AllowedContentTypes.Contains(quote.ContentType) && AllowedExtensions.Contains(extension)
            ? null
            : "Unsupported file type. Upload PDF, JPEG, PNG, or HEIC.";
    }
}
