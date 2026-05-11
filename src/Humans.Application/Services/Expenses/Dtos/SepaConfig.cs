namespace Humans.Application.Services.Expenses.Dtos;

/// <summary>
/// Configuration for SEPA Credit Transfer (pain.001) generation.
/// Bound from appsettings "Sepa" section; CreditorIban can be overridden
/// by the SEPA_CREDITOR_IBAN environment variable.
/// </summary>
public sealed class SepaConfig
{
    public string CreditorName { get; set; } = string.Empty;
    public string CreditorIban { get; set; } = string.Empty;
    public string CreditorBic { get; set; } = string.Empty;
    /// <summary>Spanish NIF or other org tax id, used as initiating-party identifier.</summary>
    public string CreditorIdentifier { get; set; } = string.Empty;
    /// <summary>"SLEV" / "SHAR" / "DEBT" — charge-bearer code for the ChrgBr element in pain.001.</summary>
    public string ChargeBearer { get; set; } = "SLEV";
}
