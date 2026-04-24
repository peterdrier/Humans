namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Google Integration section's metrics surface (issue nobodies-collective/Humans#580).
/// </summary>
public interface IGoogleSyncMetrics
{
    /// <summary>
    /// Increments <c>humans.sync_operations_total</c> with the <c>result</c>
    /// tag ("success", "failure", "permanent_failure"). Called by the
    /// outbox-processor job after each batched operation.
    /// </summary>
    void RecordSyncOperation(string result);
}
