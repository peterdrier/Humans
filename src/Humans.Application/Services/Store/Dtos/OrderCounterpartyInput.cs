namespace Humans.Application.Services.Store.Dtos;

public record OrderCounterpartyInput(
    string? Name,
    string? VatId,
    string? Address,
    string? CountryCode,
    string? Email);
