
namespace Humans.Consent.Models;

internal sealed class ConsentIndexViewModel
{
    public List<ConsentTeamGroupViewModel> TeamGroups { get; set; } = [];
    public List<ConsentHistoryViewModel> ConsentHistory { get; set; } = [];
}

internal sealed class ConsentTeamGroupViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public List<ConsentDocumentViewModel> Documents { get; set; } = [];
    public bool AllConsented => Documents.All(d => d.HasConsented);
}

internal sealed class ConsentDocumentViewModel
{
    public Guid DocumentVersionId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string VersionNumber { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public bool HasConsented { get; set; }
    public DateTime? ConsentedAt { get; set; }
    public string? ChangesSummary { get; set; }
    public DateTime? LastUpdated { get; set; }
}

internal sealed class ConsentHistoryViewModel
{
    public Guid DocumentVersionId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string VersionNumber { get; set; } = string.Empty;
    public DateTime ConsentedAt { get; set; }
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
