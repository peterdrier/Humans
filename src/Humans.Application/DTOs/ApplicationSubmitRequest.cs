using System.ComponentModel.DataAnnotations;

namespace Humans.Application.DTOs;

/// <summary>
/// Request to submit a new membership application.
/// </summary>
public record ApplicationSubmitRequest
{
    [Required(ErrorMessage = "Motivation is required")]
    [StringLength(5000, MinimumLength = 50, ErrorMessage = "Motivation must be between 50 and 5000 characters")]
    public required string Motivation { get; init; }

    [StringLength(2000, ErrorMessage = "Additional info cannot exceed 2000 characters")]
    public string? AdditionalInfo { get; init; }
}
