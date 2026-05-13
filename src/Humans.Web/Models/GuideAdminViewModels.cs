using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

public class GuideSettingsViewModel
{
    public Guid? Id { get; set; }

    [Required]
    [Display(Name = "Event Edition")]
    public Guid EventSettingsId { get; set; }

    [Required]
    [Display(Name = "Submission Opens")]
    public DateTime SubmissionOpenAt { get; set; }

    [Required]
    [Display(Name = "Submission Closes")]
    public DateTime SubmissionCloseAt { get; set; }

    [Required]
    [Display(Name = "Guide Published")]
    public DateTime GuidePublishAt { get; set; }

    [Required]
    [Range(1, 10000)]
    [Display(Name = "Max Print Slots")]
    public int MaxPrintSlots { get; set; } = 100;

    public List<EventSettingsOptionViewModel> AvailableEventSettings { get; set; } = [];
    public string? TimeZoneId { get; set; }
}

public class EventSettingsOptionViewModel
{
    public Guid Id { get; set; }
    public string EventName { get; set; } = string.Empty;
}

public class EventCategoryListViewModel
{
    public List<EventCategoryRowViewModel> Categories { get; set; } = [];
}

public class EventCategoryRowViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }
    public bool IsActive { get; set; }
    public int DisplayOrder { get; set; }
    public int EventCount { get; set; }
}

public class EventCategoryFormViewModel
{
    public Guid? Id { get; set; }

    [Required]
    [MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(60)]
    [RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", ErrorMessage = "Slug must be URL-safe (lowercase letters, numbers, hyphens).")]
    public string Slug { get; set; } = string.Empty;

    [Display(Name = "Sensitive")]
    public bool IsSensitive { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Display Order")]
    public int DisplayOrder { get; set; }
}

public class GuideVenueListViewModel
{
    public List<GuideVenueRowViewModel> Venues { get; set; } = [];
}

public class GuideVenueRowViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LocationDescription { get; set; }
    public bool IsActive { get; set; }
    public int DisplayOrder { get; set; }
    public int EventCount { get; set; }
}

public class GuideVenueFormViewModel
{
    public Guid? Id { get; set; }

    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(120)]
    [Display(Name = "Location")]
    public string? LocationDescription { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Display Order")]
    public int DisplayOrder { get; set; }
}
