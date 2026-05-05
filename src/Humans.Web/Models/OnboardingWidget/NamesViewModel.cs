using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models.OnboardingWidget;

public class NamesViewModel
{
    [Required]
    [StringLength(100)]
    [Display(Name = "Burner Name")]
    public string BurnerName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal First Name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal Last Name(s)")]
    public string LastName { get; set; } = string.Empty;
}
