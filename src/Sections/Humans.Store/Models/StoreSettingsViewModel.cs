using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Humans.Store.Models;

internal sealed class StoreSettingsViewModel
{
    public string? StripeGatewayTreasuryId { get; set; }
    [BindRequired]
    public decimal SimplifiedInvoiceThresholdEur { get; set; }
}
