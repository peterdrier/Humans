using System.ComponentModel.DataAnnotations;

namespace Humans.Users.Models;

/// <summary>The member's own medical conditions (GDPR Art. 9), on <c>/Profile/Me/Medical</c>.</summary>
internal sealed class MedicalViewModel
{
    [StringLength(4000, ErrorMessage = "Validation_MaxLength")]
    public string? MedicalConditions { get; set; }

    /// <summary>Set by the controller from the principal, never bound: it keys the "who has looked" list.</summary>
    public Guid UserId { get; set; }
}
