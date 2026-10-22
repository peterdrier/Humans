namespace Humans.Onboarding.Models;

internal sealed class ConsentsStepViewModel
{
    public required Guid DocumentVersionId { get; init; }
    public required string DocumentName { get; init; }
    public required string VersionNumber { get; init; }
    public required Dictionary<string, string> Content { get; init; }
    public string? ChangesSummary { get; init; }

    /// <summary>1-based.</summary>
    public required int CurrentIndex { get; init; }

    public required int TotalRequired { get; init; }
}
