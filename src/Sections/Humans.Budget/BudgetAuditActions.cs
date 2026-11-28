using Humans.Audit.Contracts;

namespace Humans.Budget;

/// <summary>The audit actions Budget writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class BudgetAuditActions : AuditActionContributor
{
    /// <summary>Seasonal, so kept for the retention period. Related to the change's budget year.</summary>
    public static readonly AuditActionDefinition BudgetChanged = new("BudgetChanged", AuditKept.RetentionPeriod);

    public static readonly AuditActionDefinition BudgetAuditLogCopied = new("BudgetAuditLogCopied", AuditKept.Forever);
}
