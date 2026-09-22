using NodaTime;

namespace Humans.Finance.Contracts;

public sealed record HoldedProvisioningRow(
    Guid BudgetCategoryId, string CategoryName, string GroupName,
    int? ExistingAccountNum, int? ProposedAccountNum, string Tag, string State); // Mapped|ToAdd|Orphan

public sealed record HoldedProvisioningPlan(
    IReadOnlyList<HoldedProvisioningRow> Rows, int NextNumber);

/// <summary>One budget category's Holded actual, plus the approved purchase docs it sums.
/// <c>Docs</c> is what the figure is made of — the year page renders it under the category so a
/// wrong total can be traced to the document that caused it.</summary>
public sealed record HoldedActualRow(
    Guid BudgetCategoryId, decimal Actual, IReadOnlyList<HoldedActualDoc> Docs);

/// <summary>One approved Holded purchase doc behind a category's actual.</summary>
public sealed record HoldedActualDoc(
    string HoldedDocId, string DocNumber, string ContactName, LocalDate Date,
    decimal Total, string HoldedUrl);

public sealed record HoldedUnmatchedRow(
    string HoldedDocId, string DocNumber, string ContactName, decimal Total,
    string Reason, string HoldedUrl);

public sealed record HoldedSyncResult(int DocCount, int Matched, int Unmatched);

/// <summary>State of Finance's purchase-doc sync, for the /Holded screen's sync table — the
/// one Holded-owned page that shows both syncs. <c>Status</c> is "Idle" | "Running" | "Error".</summary>
public sealed record HoldedDocSyncInfo(
    Instant? LastSyncAt, string Status, string? LastError, int LastSyncedDocCount,
    int CreditorBindingCount);
