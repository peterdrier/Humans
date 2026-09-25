using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// An expense account Finance created or linked outside the budget category map — for a
/// caller that needs "an account to book to" without a BudgetCategory behind it. Finance
/// records only the account: which caller wanted it is that caller's business. The registry
/// is what keeps these accounts' purchase docs off the Unmatched queue, and what lets a
/// retired account drop out of the pickers.
/// </summary>
internal sealed class HoldedManagedAccount
{
    public Guid Id { get; init; }
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
