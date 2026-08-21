using Humans.Base.Interfaces;
using Humans.Expenses.Services.Dtos;
using NodaTime;

namespace Humans.Expenses.Services;

/// <summary>
/// The pre-accounting layer Holded lacks (nobodies-collective/Humans#1030): a vendor commitment is
/// recorded when a quote is accepted, payments out are recorded against it, and the real invoice
/// is matched back to it from Holded. Section-internal — nothing outside Expenses consumes it.
/// </summary>
internal interface IVendorCommitmentService : IApplicationService
{
    /// <summary>False where no Holded API key is configured (PR previews, local dev) — every call
    /// would 401, so the screens hide the match action rather than offering a button that fails.</summary>
    bool MatchingAvailable { get; }

    Task<VendorCommitmentDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<VendorCommitmentDto>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// The liability list: commitments with money out and no purchase document, worst first —
    /// ordered by age × amount, so an old six-figure hole outranks a fresh small one.
    /// </summary>
    Task<IReadOnlyList<VendorCommitmentDto>> ListPaidAwaitingInvoiceAsync(CancellationToken ct = default);

    /// <summary>Records the accepted quote. The PDF is optional at creation and can be added later.</summary>
    Task<(ExpenseMutationResult Result, Guid? CommitmentId)> CreateAsync(
        string vendorName, decimal expectedAmount, string purpose,
        Guid? budgetCategoryId, Guid actorUserId,
        ExpenseFileUpload? quote = null, CancellationToken ct = default);

    Task<ExpenseMutationResult> AttachQuoteAsync(
        Guid commitmentId, Guid actorUserId, ExpenseFileUpload quote, CancellationToken ct = default);

    Task<ExpenseMutationResult> RecordPaymentAsync(
        Guid commitmentId, decimal amount, LocalDate paidOn, string? reference,
        Guid actorUserId, CancellationToken ct = default);

    /// <summary>Closes a settled or abandoned commitment. See the implementation for which
    /// statuses may close.</summary>
    Task<ExpenseMutationResult> CloseAsync(
        Guid commitmentId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Matches every open commitment against Holded's purchase documents. Links only unambiguous
    /// single fits; ties and documents for already-invoiced commitments go to the review queue.
    /// </summary>
    Task<(ExpenseMutationResult Result, VendorCommitmentMatchRunResult? Run)> RunMatchingAsync(
        Guid actorUserId, CancellationToken ct = default);

    /// <summary>Accepts (links) or dismisses one queued review row.</summary>
    Task<ExpenseMutationResult> ResolveCandidateAsync(
        Guid candidateId, bool accepted, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The stored quote file, or null when the commitment has none.</summary>
    Task<(byte[] Content, string ContentType, string FileName)?> GetQuoteFileAsync(
        Guid commitmentId, CancellationToken ct = default);
}
