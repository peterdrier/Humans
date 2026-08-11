using Humans.Finance.Domain;
using NodaTime;
namespace Humans.Finance.Domain;

/// <summary>A Holded purchase doc pulled + attributed to a budget category.</summary>
internal sealed class HoldedExpenseDoc
{
    public Guid Id { get; init; }
    public string HoldedDocId { get; set; } = "";  // unique upsert key
    public string DocNumber { get; set; } = "";
    public string ContactName { get; set; } = "";
    public LocalDate Date { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "eur";
    /// <summary>Null = row predates the v2 migration and hasn't been re-synced yet; treat as false.
    /// Nullable by rule — a required column on an existing table forces an undeclared physical
    /// default (memory/architecture/required-columns-need-approval.md).</summary>
    public bool? IsApproved { get; set; }
    public string TagsJson { get; set; } = "[]";    // raw tags, jsonb
    public string? BookedAccountId { get; set; }    // first line's account id
    public Guid? BudgetCategoryId { get; set; }     // FK-only, null = unmatched
    public HoldedMatchStatus MatchStatus { get; set; }
    public HoldedMatchSource MatchSource { get; set; }
    public string RawPayload { get; set; } = "{}";  // jsonb, debugging
    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
