using NodaTime;

namespace Humans.Finance.Contracts;

public sealed record HoldedProvisioningRow(
    Guid BudgetCategoryId, string CategoryName, string GroupName,
    int? ExistingAccountNum, int? ProposedAccountNum, string Tag, string State); // Mapped|ToAdd|Orphan

public sealed record HoldedProvisioningPlan(
    IReadOnlyList<HoldedProvisioningRow> Rows, int NextNumber);

public sealed record HoldedActualRow(Guid BudgetCategoryId, decimal Actual);

public sealed record HoldedUnmatchedRow(
    string HoldedDocId, string DocNumber, string ContactName, decimal Total,
    string Reason, string HoldedUrl);

public sealed record HoldedSyncResult(int DocCount, int Matched, int Unmatched);

/// <summary>State of Finance's purchase-doc sync, for the /Holded screen's sync table — the
/// one Holded-owned page that shows both syncs. <c>Status</c> is "Idle" | "Running" | "Error".</summary>
public sealed record HoldedDocSyncInfo(
    Instant? LastSyncAt, string Status, string? LastError, int LastSyncedDocCount,
    int CreditorBindingCount);
