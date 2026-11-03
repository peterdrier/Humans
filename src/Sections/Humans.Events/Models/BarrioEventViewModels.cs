using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.ComponentModel.DataAnnotations;
using Humans.Events.Contracts;

namespace Humans.Events.Models;

internal sealed class CampEventRowViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public EventStatus Status { get; set; }
    public int? PriorityRank { get; set; }
    public bool CanEdit { get; set; }
    public bool CanWithdraw { get; set; }

    public string StatusBadgeClass => EventStatusPresentation.BadgeClass(Status);
}

internal sealed class CampEventFormViewModel : EventProposalFormViewModel
{
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public string CampSlug { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation_Required")]
    [Range(15, 480, ErrorMessage = "Validation_Range")]
    [Display(Name = "Duration (minutes)")]
    [BindRequired]
    public int DurationMinutes { get; set; } = 60;

    [Required(ErrorMessage = "Validation_Required")]
    [Range(1, 100, ErrorMessage = "Validation_Range")]
    [Display(Name = "Priority Rank")]
    public int? PriorityRank { get; set; }
}

internal sealed class CategoryOptionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class EventDayOptionViewModel
{
    public int DayOffset { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime Date { get; set; }
}
