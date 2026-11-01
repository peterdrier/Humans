using Humans.Consent.Contracts;

namespace Humans.Onboarding.Models;

internal sealed class ConsentsStepViewModel
{
    public required ConsentReviewDetail Document { get; init; }

    /// <summary>1-based.</summary>
    public required int CurrentIndex { get; init; }

    public required int TotalRequired { get; init; }
}
