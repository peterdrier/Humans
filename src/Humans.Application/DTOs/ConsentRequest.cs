using System.ComponentModel.DataAnnotations;

namespace Humans.Application.DTOs;

/// <summary>
/// Request to record consent for a document version.
/// </summary>
public record ConsentRequest
{
    [Required(ErrorMessage = "Document version is required")]
    public required Guid DocumentVersionId { get; init; }

    [Required(ErrorMessage = "Explicit consent is required")]
    public required bool ExplicitConsent { get; init; }

    [Required(ErrorMessage = "IP address is required")]
    [StringLength(45, ErrorMessage = "IP address cannot exceed 45 characters")]
    public required string IpAddress { get; init; }

    [Required(ErrorMessage = "User agent is required")]
    [StringLength(500, ErrorMessage = "User agent cannot exceed 500 characters")]
    public required string UserAgent { get; init; }
}
