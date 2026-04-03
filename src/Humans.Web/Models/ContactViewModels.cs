using System.ComponentModel.DataAnnotations;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

public class AdminContactListViewModel
{
    public List<AdminContactViewModel> Contacts { get; set; } = [];
    public int TotalCount { get; set; }
    public string? SearchTerm { get; set; }
}

public class AdminContactViewModel
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ContactSource? ContactSource { get; set; }
    public string? ExternalSourceId { get; set; }
    public Instant CreatedAt { get; set; }
    public bool HasCommunicationPreferences { get; set; }
}

public class AdminContactDetailViewModel
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ContactSource? ContactSource { get; set; }
    public string? ExternalSourceId { get; set; }
    public Instant CreatedAt { get; set; }
    public IReadOnlyList<CommunicationPreference> CommunicationPreferences { get; set; } = [];
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
