using Humans.Domain.Enums;

namespace Humans.Monitor.Models;

internal sealed class GoogleSyncAuditEntryViewModel
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

internal sealed class GoogleSyncAuditListViewModel
{
    public List<GoogleSyncAuditEntryViewModel> Entries { get; set; } = [];
    public string Title { get; set; } = string.Empty;
    public string? BackUrl { get; set; }
    public string? BackLabel { get; set; }
}
