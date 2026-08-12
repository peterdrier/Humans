using System.ComponentModel.DataAnnotations;

namespace Humans.Onboarding.Models;

internal sealed class NamesViewModel
{
    [Required]
    [StringLength(100)]
    [Display(Name = "Onboarding_BurnerNameLabel")]
    public string BurnerName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Onboarding_LegalFirstNameLabel")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Onboarding_LegalLastNameLabel")]
    public string LastName { get; set; } = string.Empty;
}
