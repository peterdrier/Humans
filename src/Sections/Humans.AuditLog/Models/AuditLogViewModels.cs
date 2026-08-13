using Humans.Domain.Enums;

using Humans.UI.Models;

namespace Humans.AuditLog.Models;

internal sealed class AuditLogListViewModel() : PagedListViewModel(50)
{
    public IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent> Events { get; set; } = [];
    public string? ActionFilter { get; set; }
    public int AnomalyCount { get; set; }
}
