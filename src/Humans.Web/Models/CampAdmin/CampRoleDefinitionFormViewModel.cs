using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models.CampAdmin;

public sealed class CampRoleDefinitionFormViewModel : IValidatableObject
{
    public Guid? Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Range(1, 100)]
    public int SlotCount { get; set; } = 1;

    [Range(0, 100)]
    public int MinimumRequired { get; set; } = 1;

    public int SortOrder { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (MinimumRequired > SlotCount)
            yield return new ValidationResult(
                "Minimum required must be less than or equal to slot count.",
                new[] { nameof(MinimumRequired) });
    }
}
