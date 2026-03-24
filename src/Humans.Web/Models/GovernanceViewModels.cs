using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class GovernanceIndexViewModel
{
    /// <summary>
    /// Statutes content by language code (e.g., "es" → markdown, "en" → markdown).
    /// </summary>
    public Dictionary<string, string> StatutesContent { get; set; } = new(StringComparer.Ordinal);

    public bool HasApplication { get; set; }
    public ApplicationStatus? ApplicationStatus { get; set; }
    public MembershipTier? ApplicationTier { get; set; }
    public DateTime? ApplicationSubmittedAt { get; set; }
    public DateTime? ApplicationResolvedAt { get; set; }
    public string? ApplicationStatusBadgeClass { get; set; }
    public bool CanApply { get; set; }
    public bool IsApprovedColaborador { get; set; }

    // Tier member counts for the sidebar
    public int ColaboradorCount { get; set; }
    public int AsociadoCount { get; set; }
}
