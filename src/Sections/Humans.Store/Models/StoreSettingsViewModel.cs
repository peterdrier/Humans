using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

internal sealed class StoreSettingsViewModel
{
    public int? DepositLiabilityAccountNum { get; set; }
    public decimal SimplifiedInvoiceThresholdEur { get; set; }
    public IReadOnlyList<AccountChoice> DepositAccountChoices { get; init; } = [];
}
