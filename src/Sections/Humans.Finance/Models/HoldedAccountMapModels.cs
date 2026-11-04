using NodaTime;

namespace Humans.Finance.Models;

/// <summary>Created outside the budget map.</summary>
internal sealed record HoldedManagedAccountVm(int AccountNumber, string AccountId, string Label, bool IsActive, Instant CreatedAt);

internal sealed record HoldedProvisioningRow(
    Guid BudgetCategoryId, string CategoryName, string GroupName,
    int? ExistingAccountNum, int? ProposedAccountNum, string Tag, string State); // Mapped|ToAdd|Orphan

internal sealed record HoldedProvisioningPlan(
    IReadOnlyList<HoldedProvisioningRow> Rows, int NextNumber);
