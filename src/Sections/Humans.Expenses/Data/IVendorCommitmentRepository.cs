using Humans.Base.Interfaces.Repositories;
using Humans.Expenses.Domain;
using Humans.Expenses.Services.Dtos;
using NodaTime;

namespace Humans.Expenses.Data;

/// <summary>
/// Sole owner of <c>vendor_commitments</c>, <c>vendor_commitment_payments</c> and
/// <c>vendor_commitment_match_candidates</c>. Reads return fully-populated DTOs (payments and
/// match candidates always included — a commitment without them is never a useful read).
/// </summary>
internal interface IVendorCommitmentRepository : IRepository
{
    Task<VendorCommitmentDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every commitment, unordered — display order is the caller's.
    /// ~hundreds of rows, so no pagination by design.</summary>
    Task<IReadOnlyList<VendorCommitmentDto>> GetAllAsync(CancellationToken ct = default);

    Task<Guid> AddAsync(VendorCommitment commitment, CancellationToken ct = default);

    /// <summary>Records a payment and moves the commitment to its derived payment status.
    /// False when the commitment does not exist or is Closed.</summary>
    Task<bool> AddPaymentAsync(
        Guid commitmentId, VendorCommitmentPayment payment,
        VendorCommitmentStatus newStatus, Instant updatedAt, CancellationToken ct = default);

    /// <summary>Records the stored quote file's metadata. False when the commitment does not exist.</summary>
    Task<bool> SetQuoteAsync(
        Guid commitmentId, string fileName, string contentType, string extension,
        Instant uploadedAt, CancellationToken ct = default);

    /// <summary>Links the purchase document and moves the commitment to Invoiced. Also drops the
    /// review rows the link makes unacceptable: every other commitment's row for this document, and
    /// this commitment's rows for other documents. The row for this pair survives, for the caller to
    /// resolve. False when the commitment does not exist, is Closed, already carries a document, or
    /// the document already backs another commitment.</summary>
    Task<bool> LinkPurchaseDocumentAsync(
        Guid commitmentId, string holdedDocId, string holdedDocNumber,
        Instant matchedAt, CancellationToken ct = default);

    /// <summary>Closes the commitment and drops its unresolved review rows — a closed commitment
    /// can no longer accept one. Resolved rows stay: those record a human's ruling. False when it
    /// does not exist or is already Closed.</summary>
    Task<bool> CloseAsync(Guid commitmentId, Instant closedAt, CancellationToken ct = default);

    /// <summary>
    /// Upserts the pending review rows for one commitment: adds candidates the matcher just
    /// found and removes pending ones it no longer reports. Rows a human already resolved are
    /// never touched — re-running the matcher must not resurrect a dismissed decision.
    /// </summary>
    Task ReplacePendingCandidatesAsync(
        Guid commitmentId, IReadOnlyList<VendorCommitmentMatchCandidate> candidates,
        CancellationToken ct = default);

    /// <summary>Marks one candidate accepted or dismissed. False when it does not exist or was
    /// already resolved.</summary>
    Task<bool> ResolveCandidateAsync(
        Guid candidateId, bool accepted, Guid actorUserId,
        Instant resolvedAt, CancellationToken ct = default);

    /// <summary>The candidate's commitment and document identity, for the accept path.
    /// Null when unknown.</summary>
    Task<VendorCommitmentMatchCandidateDto?> GetCandidateAsync(
        Guid candidateId, CancellationToken ct = default);
}
