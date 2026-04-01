using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Humans.Web.Models;

public abstract class TeamFormViewModelBase
{
    [Required]
    [StringLength(256, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(64)]
    [RegularExpression(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", ErrorMessage = "Only lowercase letters, numbers, and hyphens allowed")]
    public string? GoogleGroupPrefix { get; set; }

    public bool RequiresApproval { get; set; } = true;

    public bool IsHidden { get; set; }

    public Guid? ParentTeamId { get; set; }

    /// <summary>
    /// Available parent teams for the dropdown.
    /// </summary>
    public List<SelectListItem> EligibleParents { get; set; } = [];
}
