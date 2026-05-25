using Humans.Domain.Enums;
using NodaTime;
namespace Humans.Domain.Entities;

/// <summary>A Holded purchase doc pulled + attributed to a budget category.</summary>
public class HoldedExpenseDoc
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
    public Instant? ApprovedAt { get; set; }
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
