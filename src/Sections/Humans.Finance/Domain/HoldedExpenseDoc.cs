using NodaTime;
namespace Humans.Finance.Domain;

internal sealed class HoldedExpenseDoc
{
    public Guid Id { get; init; }
    public string HoldedDocId { get; set; } = "";  // unique upsert key
    public string DocNumber { get; set; } = "";
    public string ContactName { get; set; } = "";
    /// <summary>Who the doc is payable to. Null when Holded names no contact, or on a row not re-synced since the
    /// column was added.</summary>
    public string? ContactId { get; set; }
    /// <summary>Null when blank.</summary>
    public string? Description { get; set; }
    public LocalDate Date { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "eur";
    /// <summary>Null: a row from before the v2 migration not yet re-synced; treat as false.</summary>
    public bool? IsApproved { get; set; }
    /// <summary>Not in the v2 list, so fetched once per document new to the mirror. Null: not fetched yet (or the fetch
    /// failed, so the next sync retries); empty: Holded holds none.</summary>
    public string? Notes { get; set; }
    public string TagsJson { get; set; } = "[]";    // raw tags, jsonb
    public string? BookedAccountId { get; set; }    // first line's account id
    public Guid? BudgetCategoryId { get; set; }     // null = unmatched
    public HoldedMatchStatus MatchStatus { get; set; }
    public HoldedMatchSource MatchSource { get; set; }
    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
