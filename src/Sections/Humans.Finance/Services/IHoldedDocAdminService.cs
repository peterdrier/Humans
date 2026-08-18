using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

/// <summary>
/// The purchase-document surface <c>FinanceController</c> uses: the cross-section contract plus
/// the treasurer's provisioning and unmatched-queue operations, which no other section calls and
/// so never cross the assembly boundary.
/// </summary>
internal interface IHoldedDocAdminService : IHoldedDocService
{
    /// <summary>Reconciles the live Holded chart of accounts against holded_category_map:
    /// Mapped / ToAdd / Orphan, plus the next free account number.</summary>
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);

    /// <summary>Creates the plan's ToAdd accounts in Holded and maps them locally. Additive only;
    /// <paramref name="addAll"/> false does one, for a test run.</summary>
    Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default);

    /// <summary>The unmatched-doc worklist, each row carrying why it did not attribute.</summary>
    Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default);
}
