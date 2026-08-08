namespace Humans.Store.Services.Dtos;

public record OrderCounterpartyInput(
    string? Name,
    string? VatId,
    string? Address,
    string? CountryCode,
    string? Email);
