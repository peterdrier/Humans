using System.ComponentModel.DataAnnotations;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class AdminContactListViewModel : PagedListViewModel
{
    public AdminContactListViewModel() : base(20) { }

    public List<AdminContactViewModel> Contacts { get; set; } = [];
    public string? SearchTerm { get; set; }
}

public class AdminContactViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ContactSource? ContactSource { get; set; }
    public string? ExternalSourceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool HasCommunicationPreferences { get; set; }
}

public class AdminContactDetailViewModel
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ContactSource? ContactSource { get; set; }
    public string? ExternalSourceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public IReadOnlyList<CommunicationPreference> CommunicationPreferences { get; set; } = [];
    public IReadOnlyList<AuditLogEntry> AuditLog { get; set; } = [];
}

public class CreateContactViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public ContactSource Source { get; set; } = ContactSource.Manual;
}
