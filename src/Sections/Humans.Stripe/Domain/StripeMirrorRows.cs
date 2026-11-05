using NodaTime;

namespace Humans.Stripe.Domain;

/// <summary>One Stripe balance transaction: a line of an account's ledger. A cache row, rewritten by every sweep.</summary>
internal sealed class StripeBalanceTransaction
{
    public string Id { get; init; } = "";
    public string AccountId { get; set; } = "";

    public string Type { get; set; } = "";
    public string ReportingCategory { get; set; } = "";
    public string Status { get; set; } = "";
    public string Currency { get; set; } = "";

    /// <summary>Minor units, signed as Stripe gives them.</summary>
    public long Amount { get; set; }

    public long Fee { get; set; }
    public long Net { get; set; }

    public Instant Created { get; set; }

    /// <summary>When the amount reaches the available balance. A reserve's hold and release are created together; this
    /// is when each takes effect.</summary>
    public Instant AvailableOn { get; set; }

    public string? Description { get; set; }
    public string? SourceId { get; set; }

    /// <summary>The automatic payout that paid this out. Null for a manual-payout account: Stripe links no transaction
    /// to a manual payout.</summary>
    public string? PayoutId { get; set; }

    /// <summary>From the source charge (or a sibling row on the same PaymentIntent, for a refund), for search.</summary>
    public string? PaymentIntentId { get; set; }

    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }

    /// <summary>The source charge's metadata (order ids), as JSON; null when it has none.</summary>
    public string? Metadata { get; set; }

    /// <summary>The transaction as Stripe returned it, its source collapsed back to the id.</summary>
    public string Raw { get; set; } = "";

    public Instant SyncedAt { get; set; }
}

internal sealed class StripePayout
{
    public string Id { get; init; } = "";
    public string AccountId { get; set; } = "";
    public long Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Automatic { get; set; }
    public string? Method { get; set; }
    public Instant Created { get; set; }
    public Instant ArrivalDate { get; set; }
    public string? BalanceTransactionId { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string? StatementDescriptor { get; set; }
    public Instant SyncedAt { get; set; }
}

/// <summary>An account's balance in one currency at the last sweep.</summary>
internal sealed class StripeBalance
{
    public string AccountId { get; init; } = "";
    public string Currency { get; init; } = "";
    public long Available { get; set; }
    public long Pending { get; set; }
    public Instant SyncedAt { get; set; }
}

internal enum StripeSyncStatus { Idle, Running, Error }

/// <summary>One row per mirrored account; an account never swept has none.</summary>
internal sealed class StripeSyncState
{
    public string AccountId { get; init; } = "";
    public StripeSyncStatus SyncStatus { get; set; } = StripeSyncStatus.Idle;
    public Instant? LastSyncAt { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastCount { get; set; }
    public string? LastError { get; set; }
}
