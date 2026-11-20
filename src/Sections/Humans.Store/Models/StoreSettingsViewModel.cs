namespace Humans.Store.Models;

internal sealed class StoreSettingsViewModel
{
    public string? StripeGatewayTreasuryId { get; set; }
    public decimal SimplifiedInvoiceThresholdEur { get; set; }
}
