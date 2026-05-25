namespace Humans.Application.Services.Finance.Dtos;

public sealed record HoldedProvisioningRow(
    Guid BudgetCategoryId, string CategoryName, string GroupName,
    int? ExistingAccountNum, int? ProposedAccountNum, string Tag, string State); // Mapped|ToAdd|Orphan

public sealed record HoldedProvisioningPlan(
    IReadOnlyList<HoldedProvisioningRow> Rows, int NextNumber);

public sealed record HoldedActualRow(Guid BudgetCategoryId, decimal Actual, int DocCount);

public sealed record HoldedUnmatchedRow(
    string HoldedDocId, string DocNumber, string ContactName, decimal Total,
    string Reason, string HoldedUrl);

public sealed record HoldedSyncResult(int DocCount, int Matched, int Unmatched);
