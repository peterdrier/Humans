using NodaTime;

namespace Humans.Pleo.Domain;

/// <summary>One Pleo accounting entry. A cache row: every sweep rewrites it from the API.</summary>
internal sealed class PleoEntry
{
    public string Id { get; init; } = "";

    public string? EmployeeId { get; set; }
    public string? TeamId { get; set; }

    /// <summary>Pleo's <c>accountId</c>. Despite the name it is a category id; the category's Holded code comes from
    /// <see cref="PleoCategory"/>, because the API that maps categories to accounts is closed to this key.</summary>
    public string? CategoryId { get; set; }

    public string? Family { get; set; }
    public string? SubFamily { get; set; }
    public string? Status { get; set; }
    public string? ExportStatus { get; set; }
    public string? ReviewStatus { get; set; }

    public Instant? PerformedAt { get; set; }
    public Instant? SettledAt { get; set; }
    public Instant? BookkeepingDate { get; set; }
    public Instant? CreatedAt { get; set; }
    public Instant? UpdatedAt { get; set; }

    /// <summary><c>transactionValue</c> as given: unsigned, in the purchase currency.</summary>
    public string Currency { get; set; } = "";

    public long Minors { get; set; }

    /// <summary>Signed EUR (+ into the wallet, − out); null when Pleo gives no EUR figure. See
    /// <see cref="PleoEntryRules.SignedEur"/>.</summary>
    public decimal? AmountEur { get; set; }

    public string? MerchantName { get; set; }
    public string? MerchantCategoryCode { get; set; }
    public string? MerchantCountry { get; set; }
    public string? Note { get; set; }
    public int ReceiptCount { get; set; }

    /// <summary>The entry exactly as the API returned it, so the detail page shows every field, modelled or not.</summary>
    public string Raw { get; set; } = "";

    public Instant SyncedAt { get; set; }
}
