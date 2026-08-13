using Humans.Finance.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Application.Interfaces;

namespace Humans.Expenses.Services;

internal interface IExpenseReportService : IExpenseReportServiceRead, IApplicationService
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

    Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default);

}

internal sealed record ExpenseMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static ExpenseMutationResult Success { get; } = new(true, null);

    public static ExpenseMutationResult Failure(string message) => new(false, message);
}

internal sealed record ExpenseIbanSaveResult(
    bool Succeeded,
    bool IsValidationError,
    string Message);
