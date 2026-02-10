namespace Humans.Web.Models;

public class ConsentIndexViewModel
{
    public List<ConsentDocumentViewModel> RequiredDocuments { get; set; } = [];
    public List<ConsentHistoryViewModel> ConsentHistory { get; set; } = [];
}

public class ConsentDocumentViewModel
{
    public Guid DocumentVersionId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string VersionNumber { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public bool HasConsented { get; set; }
    public DateTime? ConsentedAt { get; set; }
    public string? ChangesSummary { get; set; }
}

public class ConsentHistoryViewModel
{
    public string DocumentName { get; set; } = string.Empty;
    public string VersionNumber { get; set; } = string.Empty;
    public DateTime ConsentedAt { get; set; }
}

public class ConsentDetailViewModel
{
    public Guid DocumentVersionId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string VersionNumber { get; set; } = string.Empty;
    public string ContentSpanish { get; set; } = string.Empty;
    public string ContentEnglish { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public string? ChangesSummary { get; set; }
    public bool HasAlreadyConsented { get; set; }
}

public class ConsentSubmitModel
{
    public Guid DocumentVersionId { get; set; }
    public bool ExplicitConsent { get; set; }
}
