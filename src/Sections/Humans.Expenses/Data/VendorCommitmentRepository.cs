using Humans.Expenses.Domain;
using Humans.Expenses.Services.Dtos;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Expenses.Data;

internal sealed class VendorCommitmentRepository(IDbContextFactory<ExpensesDbContext> factory)
    : IVendorCommitmentRepository
{
    public async Task<VendorCommitmentDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entity = await Query(ctx).FirstOrDefaultAsync(c => c.Id == id, ct);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<IReadOnlyList<VendorCommitmentDto>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entities = await Query(ctx)
            // arch:db-sort-ok registry list — newest commitments on top by default
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToDto).ToList();
    }

    public async Task<Guid> AddAsync(VendorCommitment commitment, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.VendorCommitments.Add(commitment);
        await ctx.SaveChangesAsync(ct);
        return commitment.Id;
    }

    public async Task<bool> AddPaymentAsync(
        Guid commitmentId, VendorCommitmentPayment payment,
        VendorCommitmentStatus newStatus, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var commitment = await ctx.VendorCommitments
            .FirstOrDefaultAsync(c => c.Id == commitmentId, ct);
        if (commitment is null || commitment.Status == VendorCommitmentStatus.Closed) return false;

        payment.VendorCommitmentId = commitmentId;
        ctx.VendorCommitmentPayments.Add(payment);
        commitment.Status = newStatus;
        commitment.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetQuoteAsync(
        Guid commitmentId, string fileName, string contentType, string extension,
        Instant uploadedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var commitment = await ctx.VendorCommitments
            .FirstOrDefaultAsync(c => c.Id == commitmentId, ct);
        if (commitment is null) return false;

        commitment.QuoteFileName = fileName;
        commitment.QuoteContentType = contentType;
        commitment.QuoteExtension = extension;
        commitment.QuoteUploadedAt = uploadedAt;
        commitment.UpdatedAt = uploadedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> LinkPurchaseDocumentAsync(
        Guid commitmentId, string holdedDocId, string holdedDocNumber,
        Instant matchedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var commitment = await ctx.VendorCommitments
            .FirstOrDefaultAsync(c => c.Id == commitmentId, ct);
        // Already carrying a document is the dupe case — the caller queues a review row instead.
        if (commitment is null || commitment.MatchedHoldedDocId is not null) return false;

        commitment.MatchedHoldedDocId = holdedDocId;
        commitment.MatchedHoldedDocNumber = holdedDocNumber;
        commitment.MatchedAt = matchedAt;
        commitment.Status = VendorCommitmentStatus.Invoiced;
        commitment.UpdatedAt = matchedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> CloseAsync(Guid commitmentId, Instant closedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var commitment = await ctx.VendorCommitments
            .FirstOrDefaultAsync(c => c.Id == commitmentId, ct);
        if (commitment is null || commitment.Status == VendorCommitmentStatus.Closed) return false;

        commitment.Status = VendorCommitmentStatus.Closed;
        commitment.ClosedAt = closedAt;
        commitment.UpdatedAt = closedAt;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReplacePendingCandidatesAsync(
        Guid commitmentId, IReadOnlyList<VendorCommitmentMatchCandidate> candidates,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var existing = await ctx.VendorCommitmentMatchCandidates
            .Where(c => c.VendorCommitmentId == commitmentId)
            .ToListAsync(ct);

        var wanted = candidates.ToDictionary(c => c.HoldedDocId, StringComparer.Ordinal);

        foreach (var row in existing)
        {
            // A human already ruled on this document; the matcher does not get to re-ask.
            if (row.ResolvedAt is not null)
            {
                wanted.Remove(row.HoldedDocId);
                continue;
            }

            if (wanted.Remove(row.HoldedDocId, out var fresh))
            {
                row.Kind = fresh.Kind;
                row.HoldedDocNumber = fresh.HoldedDocNumber;
                row.ContactName = fresh.ContactName;
                row.DocDate = fresh.DocDate;
                row.DocTotal = fresh.DocTotal;
                row.DetectedAt = fresh.DetectedAt;
            }
            else
            {
                ctx.VendorCommitmentMatchCandidates.Remove(row);
            }
        }

        foreach (var fresh in wanted.Values)
        {
            fresh.VendorCommitmentId = commitmentId;
            ctx.VendorCommitmentMatchCandidates.Add(fresh);
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<Guid?> ResolveCandidateAsync(
        Guid candidateId, bool accepted, Guid actorUserId,
        Instant resolvedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.VendorCommitmentMatchCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        if (row is null || row.ResolvedAt is not null) return null;

        row.Accepted = accepted;
        row.ResolvedAt = resolvedAt;
        row.ResolvedByUserId = actorUserId;
        await ctx.SaveChangesAsync(ct);
        return row.VendorCommitmentId;
    }

    public async Task<VendorCommitmentMatchCandidateDto?> GetCandidateAsync(
        Guid candidateId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.VendorCommitmentMatchCandidates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        return row is null ? null : ToDto(row);
    }

    private static IQueryable<VendorCommitment> Query(ExpensesDbContext ctx) =>
        ctx.VendorCommitments.AsNoTracking()
            .Include(c => c.Payments)
            .Include(c => c.MatchCandidates);

    private static VendorCommitmentDto ToDto(VendorCommitment e) =>
        new()
        {
            Id = e.Id,
            VendorName = e.VendorName,
            HoldedContactId = e.HoldedContactId,
            ExpectedAmount = e.ExpectedAmount,
            Currency = e.Currency,
            Purpose = e.Purpose,
            BudgetCategoryId = e.BudgetCategoryId,
            Status = e.Status,
            QuoteFileName = e.QuoteFileName,
            QuoteContentType = e.QuoteContentType,
            QuoteExtension = e.QuoteExtension,
            QuoteUploadedAt = e.QuoteUploadedAt,
            MatchedHoldedDocId = e.MatchedHoldedDocId,
            MatchedHoldedDocNumber = e.MatchedHoldedDocNumber,
            MatchedAt = e.MatchedAt,
            CreatedByUserId = e.CreatedByUserId,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
            ClosedAt = e.ClosedAt,
            // Unordered on purpose: display ordering belongs to the read model and the views,
            // not here (memory/architecture/display-sort-in-controllers.md).
            Payments = e.Payments
                .Select(p => new VendorCommitmentPaymentDto(
                    p.Id, p.Amount, p.PaidOn, p.Reference, p.RecordedByUserId, p.CreatedAt))
                .ToList(),
            MatchCandidates = e.MatchCandidates.Select(ToDto).ToList(),
        };

    private static VendorCommitmentMatchCandidateDto ToDto(VendorCommitmentMatchCandidate c) =>
        new(c.Id, c.HoldedDocId, c.HoldedDocNumber, c.ContactName, c.DocDate, c.DocTotal,
            c.Kind, c.DetectedAt, c.Accepted, c.ResolvedAt);
}
