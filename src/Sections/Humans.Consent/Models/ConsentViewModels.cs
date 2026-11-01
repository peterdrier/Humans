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
    public string? ReturnUrl { get; set; }
}

internal sealed class ConsentSubmitModel
{
    public Guid DocumentVersionId { get; set; }
    public bool ExplicitConsent { get; set; }

    /// <summary>Where a signature lands instead of <c>/Consent</c>; only a local URL is honoured.</summary>
    public string? ReturnUrl { get; set; }
}

/// <summary>The signing form behind both <c>/Consent/Review</c> and <see cref="ViewComponents.ConsentSigningStepViewComponent"/>.</summary>
internal sealed class ConsentReviewFormViewModel
{
    public required Guid DocumentVersionId { get; init; }
    public required Dictionary<string, string> Content { get; init; }
    public string? ChangesSummary { get; init; }
    public required string SubmitButtonText { get; init; }
    public string? ReturnUrl { get; init; }
    public string? CancelHref { get; init; }
    public string? CancelText { get; init; }
}

internal sealed class MyDataViewModel
{
    public bool IsDeletionPending { get; set; }
    public DateTime? DeletionRequestedAt { get; set; }
    public DateTime? DeletionScheduledFor { get; set; }
    public IReadOnlyList<ConsentDashboardHistoryItem> SignedDocuments { get; set; } = [];
}
