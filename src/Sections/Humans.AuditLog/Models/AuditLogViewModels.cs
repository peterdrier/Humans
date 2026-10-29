using Humans.Base.Models;

namespace Humans.AuditLog.Models;

internal sealed class AuditLogListViewModel : PagedListViewModel
{
    public IReadOnlyList<Contracts.AuditEvent> Events { get; set; } = [];
    public string? ActionFilter { get; set; }
    public int AnomalyCount { get; set; }
}
