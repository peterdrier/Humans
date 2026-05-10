namespace Humans.Application.Services.Expenses.Dtos;

public sealed record SepaConfig
{
    public required string CreditorName { get; init; }
    public required string CreditorIban { get; init; }
    public required string CreditorBic { get; init; }
    /// <summary>Spanish NIF or other org tax id, used as initiating-party identifier.</summary>
    public required string CreditorIdentifier { get; init; }
    /// <summary>"SLEV" / "SHAR" / "DEBT" — service level for charge bearer in pain.001.</summary>
    public string ChargeBearer { get; init; } = "SLEV";
}
