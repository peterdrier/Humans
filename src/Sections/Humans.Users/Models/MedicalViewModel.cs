using System.ComponentModel.DataAnnotations;

namespace Humans.Users.Models;

/// <summary>The member's own medical conditions (GDPR Art. 9), on <c>/Profile/Me/Medical</c>.</summary>
internal sealed class MedicalViewModel
{
    [StringLength(4000, ErrorMessage = "Validation_MaxLength")]
    public string? MedicalConditions { get; set; }
}
