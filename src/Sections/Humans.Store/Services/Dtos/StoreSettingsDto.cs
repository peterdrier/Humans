namespace Humans.Store.Services.Dtos;

internal sealed record StoreSettingsDto(string? StripeGatewayTreasuryId, decimal SimplifiedInvoiceThresholdEur);
