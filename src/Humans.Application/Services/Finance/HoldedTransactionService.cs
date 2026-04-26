using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Finance;

/// <summary>
/// Thin pass-through over <see cref="IHoldedRepository"/> that projects
/// <see cref="HoldedTransaction"/> entities into <see cref="HoldedTransactionDto"/>
/// for the views. Renders the persisted match status as a plain-language string
/// and synthesises the Holded deep-link URL.
/// </summary>
public sealed class HoldedTransactionService : IHoldedTransactionService
{
    private readonly IHoldedRepository _repository;

    public HoldedTransactionService(IHoldedRepository repository) => _repository = repository;

    public async Task<IReadOnlyList<HoldedTransactionDto>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        var rows = await _repository.GetUnmatchedAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<HoldedTransactionDto>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default)
    {
        var rows = await _repository.GetByCategoryAsync(budgetCategoryId, ct);
        return rows.Select(ToDto).ToList();
    }

    public Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default)
        => _repository.GetActualSumsByCategoryAsync(budgetYearId, ct);

    public Task<int> CountUnmatchedAsync(CancellationToken ct = default) => _repository.CountUnmatchedAsync(ct);

    private static HoldedTransactionDto ToDto(HoldedTransaction t) => new(
        HoldedDocId: t.HoldedDocId,
        HoldedDocNumber: t.HoldedDocNumber,
        ContactName: t.ContactName,
        Date: t.AccountingDate ?? t.Date,
        Total: t.Total,
        PaymentsTotal: t.PaymentsTotal,
        PaymentsPending: t.PaymentsPending,
        Currency: t.Currency,
        Approved: t.ApprovedAt is not null,
        Tags: t.Tags,
        MatchStatusReason: PlainLanguage(t.MatchStatus, t.Tags),
        HoldedDeepLinkUrl: $"https://app.holded.com/invoicing/purchases/{t.HoldedDocId}",
        BudgetCategoryId: t.BudgetCategoryId);

    private static string PlainLanguage(HoldedMatchStatus status, IReadOnlyList<string> tags) => status switch
    {
        HoldedMatchStatus.Matched => "Matched",
        HoldedMatchStatus.NoTags => "No tags",
        HoldedMatchStatus.UnknownTag => $"Tag(s) not found: {string.Join(", ", tags)}",
        HoldedMatchStatus.MultiMatchConflict => $"Multiple tags resolve to different categories: {string.Join(", ", tags)}",
        HoldedMatchStatus.NoBudgetYearForDate => "No budget year covers this document's date",
        HoldedMatchStatus.UnsupportedCurrency => "Currency is not EUR",
        _ => status.ToString(),
    };
}
