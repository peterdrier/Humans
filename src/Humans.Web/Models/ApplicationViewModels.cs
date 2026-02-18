using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class ApplicationIndexViewModel
{
    public List<ApplicationSummaryViewModel> Applications { get; set; } = [];
    public bool CanSubmitNew { get; set; }
}

public class ApplicationSummaryViewModel
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public MembershipTier MembershipTier { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string StatusBadgeClass { get; set; } = "bg-secondary";
}

public class ApplicationDetailViewModel
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Motivation { get; set; } = string.Empty;
    public string? AdditionalInfo { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ReviewStartedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ReviewerName { get; set; }
    public string? ReviewNotes { get; set; }
    public bool CanWithdraw { get; set; }
    public List<ApplicationHistoryViewModel> History { get; set; } = [];
}

public class ApplicationHistoryViewModel
{
    public string Status { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class ApplicationCreateViewModel
{
    [Required]
    public MembershipTier MembershipTier { get; set; } = MembershipTier.Colaborador;

    [Required]
    [StringLength(2000, MinimumLength = 50)]
    [Display(Name = "Why do you want to join?")]
    public string Motivation { get; set; } = string.Empty;

    [StringLength(1000)]
    [Display(Name = "Additional Information (optional)")]
    public string? AdditionalInfo { get; set; }

    [Required]
    [Display(Name = "I confirm that the information provided is accurate")]
    public bool ConfirmAccuracy { get; set; }
}
