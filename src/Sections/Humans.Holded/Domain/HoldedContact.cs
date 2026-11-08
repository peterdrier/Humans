using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>One Holded contact as the list endpoint returns it. The whole list is the truth: a contact Holded no
/// longer lists is deleted.</summary>
internal sealed class HoldedContact
{
    /// <summary>Holded's contact id.</summary>
    public string Id { get; init; } = "";

    public string? Name { get; set; }
    public int? SupplierAccountNum { get; set; }
    public string? TradeName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Iban { get; set; }
    public string? TaxCode { get; set; }
    public string? Address { get; set; }

    public Instant LastSyncedAt { get; set; }
}
