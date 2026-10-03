using Humans.Consent.Services;

namespace Humans.Consent.Models;

internal sealed class ConsentIndexViewModel
{
    public IReadOnlyList<ConsentDashboardTeamGroup> TeamGroups { get; set; } = [];
    public IReadOnlyList<ConsentDashboardHistoryItem> ConsentHistory { get; set; } = [];
}

internal sealed class ConsentDetailViewModel
{
    public Guid DocumentVersionId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string VersionNumber { get; set; } = string.Empty;
    public Dictionary<string, string> Content { get; set; } = new(StringComparer.Ordinal);
    public DateTime EffectiveFrom { get; set; }
    public string? ChangesSummary { get; set; }
    public bool HasAlreadyConsented { get; set; }
    public string? ConsentedByFullName { get; set; }
    public DateTime? ConsentedAt { get; set; }
}

internal sealed class ConsentSubmitModel
{
    public Guid DocumentVersionId { get; set; }
    public bool ExplicitConsent { get; set; }
}
