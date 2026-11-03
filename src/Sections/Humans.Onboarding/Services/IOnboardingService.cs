using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Onboarding.Contracts;

namespace Humans.Onboarding.Services;

internal enum NextConsentStepOutcome
{
    Document,

    /// <summary>The consent-suspension self-heal has already run.</summary>
    NothingLeftToSign,

    /// <summary>
    /// A document is unsigned but couldn't be loaded. Don't hand back to the dispatcher: it still answers
    /// "consents", so the two would bounce the user forever.
    /// </summary>
    DocumentUnavailable,
}

/// <summary>
/// <see cref="Next"/> is non-null exactly when <see cref="Outcome"/> is <see cref="NextConsentStepOutcome.Document"/>.
/// <see cref="CurrentIndex"/> is 1-based.
/// </summary>
internal sealed record NextConsentStepData(
    NextConsentStepOutcome Outcome,
    ConsentReviewDetail? Next,
    int CurrentIndex,
    int TotalRequired);

internal interface IOnboardingService : IOnboardingIntake, IOrchestrator
{
    /// <summary>
    /// When nothing is left to sign, also restores a consent-suspended user who is already compliant (the
    /// required set shrank): no fresh signature will ever trigger the restore in <c>SubmitConsentAsync</c>.
    /// </summary>
    Task<NextConsentStepData> GetNextUnsignedConsentAsync(
        Guid userId, CancellationToken ct = default);
}
