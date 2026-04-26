using Humans.Application.DTOs.Finance;
using Humans.Domain.Entities;

namespace Humans.Web.ViewModels.Finance;

public sealed record HoldedUnmatchedViewModel(
    IReadOnlyList<HoldedTransactionDto> Unmatched,
    IReadOnlyList<BudgetCategory> AvailableCategories);
