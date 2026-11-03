using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>One Holded purchase document as the list endpoint returns it, plus the notes a separate GET returns.
/// The whole list is the truth: a document Holded no longer lists is deleted.</summary>
internal sealed class HoldedPurchaseDoc
{
    /// <summary>Holded's document id.</summary>
    public string Id { get; init; } = "";

    public string DocNumber { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string? ContactId { get; set; }
    public string? Description { get; set; }

    /// <summary>The document's Europe/Madrid date.</summary>
    public LocalDate Date { get; set; }

    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "eur";

    /// <summary>Holded's <c>draft</c> is false. An absent <c>draft</c> is not approved.</summary>
    public bool IsApproved { get; set; }

    /// <summary>Null until a GET has read it; the list endpoint does not return notes.</summary>
    public string? Notes { get; set; }

    /// <summary>The document's tags and its lines' tags, as Holded spells them.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>The first line's account id.</summary>
    public string? BookedAccountId { get; set; }

    public Instant LastSyncedAt { get; set; }
}
