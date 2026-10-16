using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Models;

internal sealed class ApplicationIndexViewModel
{
    public List<ApplicationSummaryViewModel> Applications { get; set; } = [];
    public bool CanSubmitNew { get; set; }
    public bool IsApprovedColaborador { get; set; }
}

internal sealed class ApplicationSummaryViewModel
{
    public Guid Id { get; set; }
    public ApplicationStatus Status { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public LocalDate? TermExpiresAt { get; set; }
    public string StatusBadgeClass { get; set; } = "bg-secondary";
}

internal abstract class ApplicationDetailViewModelBase
{
    public Guid Id { get; set; }
    public ApplicationStatus Status { get; set; }
    public string Motivation { get; set; } = string.Empty;
    public string? AdditionalInfo { get; set; }
    public string? SignificantContribution { get; set; }
    public string? RoleUnderstanding { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ReviewStartedAt { get; set; }
    public string? ReviewerName { get; set; }
    public string? ReviewNotes { get; set; }
    public List<ApplicationHistoryViewModel> History { get; set; } = [];
}

internal sealed class ApplicationDetailViewModel : ApplicationDetailViewModelBase
{
    public DateTime? ResolvedAt { get; set; }
    public bool CanWithdraw { get; set; }
}

internal sealed class ApplicationHistoryViewModel
{
    public ApplicationStatus Status { get; set; }
    public DateTime ChangedAt { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

internal sealed class ApplicationCreateViewModel
{
    public MembershipTier MembershipTier { get; set; } = MembershipTier.Colaborador;

    public string? Motivation { get; set; } = string.Empty;

    public string? AdditionalInfo { get; set; }

    /// <summary>
    /// Asociado-only: significant contribution to Nowhere or another Burn.
    /// </summary>
    public string? SignificantContribution { get; set; }

    /// <summary>
    /// Asociado-only: understanding of the asociado role and why they want it.
    /// </summary>
    public string? RoleUnderstanding { get; set; }

    public bool ConfirmAccuracy { get; set; }
}
