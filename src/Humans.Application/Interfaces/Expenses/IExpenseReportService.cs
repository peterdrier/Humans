using Humans.Application.Services.Expenses.Dtos;

namespace Humans.Application.Interfaces.Expenses;

public interface IExpenseReportService
{
    Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetApprovedUnpaidAsync(CancellationToken ct = default);

    Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task UpdateDraftAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task<Guid> AddLineAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default);

    Task UpdateLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task RemoveLineAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default);

    Task AttachToLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, Guid attachmentId,
        CancellationToken ct = default);

    Task<bool> SubmitAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default);

    Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default);

    Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default);

    Task<bool> CategoryOverrideAsync(
        Guid reportId, Guid actorUserId, Guid newCategoryId,
        CancellationToken ct = default);

    Task<int> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds, Guid actorUserId,
        CancellationToken ct = default);

    Task<bool> MarkPaidAsync(
        Guid reportId, CancellationToken ct = default);

    /// <summary>True iff the category has at least one budget coordinator
    /// (so the Submitted -> CoordinatorEndorsed step is required).</summary>
    Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
        Guid categoryId, CancellationToken ct = default);
}
