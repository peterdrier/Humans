using NodaTime;
namespace Humans.Finance.Domain;

internal sealed class HoldedCategoryMap
{
    public Guid Id { get; init; }
    public Guid BudgetCategoryId { get; set; }
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public string Tag { get; set; } = "";          // dash-free normalized fallback key
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
