using Humans.AuditLog.Contracts;
using Humans.Base.Models;

namespace Humans.AuditLog.Models;

internal sealed class AuditLogListViewModel() : PagedListViewModel(50)
{
    public IReadOnlyList<AuditEvent> Events { get; set; } = [];
    public string? ActionFilter { get; set; }

    /// <summary>Every declared action with a quick-filter label, in registry order.</summary>
    public IReadOnlyList<AuditActionDefinition> QuickFilters { get; set; } = [];

    public IReadOnlyList<AuditActionDefinition> Alerts { get; set; } = [];

    /// <summary>Rows per alert action over the whole log, keyed by name.</summary>
    public IReadOnlyDictionary<string, int> AlertCounts { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public int AlertCount(AuditActionDefinition action) =>
        AlertCounts.TryGetValue(action.Name, out var count) ? count : 0;
}
