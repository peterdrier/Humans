using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class AuditLogListViewModel : PagedListViewModel
{
    public AuditLogListViewModel() : base(50)
    {
    }

    public IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent> Events { get; set; } = [];
    public string? ActionFilter { get; set; }
    public int AnomalyCount { get; set; }
}

public class GoogleSyncAuditEntryViewModel
{
    public AuditAction Action { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? UserEmail { get; set; }
    public string? Role { get; set; }
    public GoogleSyncSource? SyncSource { get; set; }
    public DateTime OccurredAt { get; set; }
    public bool? Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResourceName { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? RelatedEntityId { get; set; }
}

public class GoogleSyncAuditListViewModel
{
    public List<GoogleSyncAuditEntryViewModel> Entries { get; set; } = [];
    public string Title { get; set; } = string.Empty;
    public string? BackUrl { get; set; }
    public string? BackLabel { get; set; }
}
