using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Humans.Store.Models;

internal sealed class StoreSettingsViewModel
{
    public string? StripeGatewayTreasuryId { get; set; }
    [BindRequired]
    [ModelBinder(BinderType = typeof(HtmlDecimalBinder))]
    public decimal SimplifiedInvoiceThresholdEur { get; set; }
}
