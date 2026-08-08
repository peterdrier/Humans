namespace Humans.Store.Services.Dtos;

internal sealed record OrderCounterpartyInput(
    string? Name,
    string? VatId,
    string? Address,
    string? CountryCode,
    string? Email);
