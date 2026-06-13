using Humans.Application.Services.Finance.Dtos;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Expenses;

public interface IExpenseReportService : IExpenseReportServiceRead, IApplicationService
{
    Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default);

    /// <summary>
    /// Removes the file, unlinks the attachment from the line, and deletes the attachment row.
    /// Authorizes: submitter ownership + editable status + line belongs to report.
    /// Idempotent — no-op if the line has no attachment.
    /// </summary>
    Task RemoveAttachmentFromLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, CancellationToken ct = default);

    Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid submitterUserId, string? iban, CancellationToken ct = default);

    Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default);

    Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default);

    /// <summary>
    /// One domain operation for the SEPA payout (M2+M3): re-checks Approved
    /// eligibility on fresh state, builds the pain.001 XML <b>before</b> flipping
    /// the included reports to <see cref="ExpenseReportStatus.SepaSent"/> — so a
    /// failure at either step leaves every report Approved and the batch
    /// regenerable — then flips and audits. Caller authorizes per report and
    /// passes only the ids it allowed.
    /// </summary>
    Task<SepaPayoutResult> GenerateSepaPayoutAsync(
        IReadOnlyCollection<Guid> reportIds, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Reverts a <see cref="ExpenseReportStatus.SepaSent"/> report back to
    /// <see cref="ExpenseReportStatus.Approved"/> so the admin can include it in a fresh SEPA
    /// batch after a failed download. Returns <see cref="ExpenseMutationResult.Failure"/> if the
    /// report is not in <c>SepaSent</c> status.
    /// </summary>
    Task<ExpenseMutationResult> ReopenSepaWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default);

    Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default);

}

public sealed record ExpenseAttachmentDownload(
    byte[] Bytes,
    string ContentType,
    string OriginalFileName);

public sealed record ExpenseMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static ExpenseMutationResult Success { get; } = new(true, null);

    public static ExpenseMutationResult Failure(string message) => new(false, message);
}

public sealed record ExpenseIbanSaveResult(
    bool Succeeded,
    bool IsValidationError,
    string Message);

/// <summary>Result of <see cref="IExpenseReportService.GenerateSepaPayoutAsync"/>.</summary>
public sealed record SepaPayoutResult(
    bool Succeeded,
    string? ErrorMessage,
    string? Xml,
    string? FileName,
    IReadOnlyList<Guid> FlippedIds)
{
    public static SepaPayoutResult Failure(string message) => new(false, message, null, null, []);

    public static SepaPayoutResult Success(string xml, string fileName, IReadOnlyList<Guid> flippedIds) =>
        new(true, null, xml, fileName, flippedIds);
}

/// <summary>Round-trip timeline for the submitter, sourced from the Holded creditor balance.</summary>
public sealed record ExpenseHoldedTimeline(
    bool RegisteredInHolded,
    decimal OwedToMember,
    decimal MemberRegisteredTotal,   // sum of this member's registered-but-unpaid ER totals
    decimal OtherAmount,             // max(0, OwedToMember - MemberRegisteredTotal): fronted / adjustments
    bool Paid,
    NodaTime.LocalDate? PaidOn,
    decimal TotalPaid,
    IReadOnlyList<HoldedPaymentInfo> Payments);
