using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Onboarding.Contracts;

namespace Humans.Onboarding.Services;

internal sealed record BulkOnboardingResult(int ApprovedCount);

/// <summary>How the consent step resolved. The three cases route three different ways.</summary>
internal enum NextConsentStepOutcome
{
    /// <summary>There is a document to show; <c>Next</c> carries it.</summary>
    Document,

    /// <summary>
    /// Everything required is signed. The consent-suspension self-heal has already run and
    /// the caller can hand back to the dispatcher.
    /// </summary>
    NothingLeftToSign,

    /// <summary>
    /// A document is still unsigned but its detail could not be loaded. The caller must NOT
    /// hand back to the dispatcher: the dispatcher's answer is still "consents", so the two
    /// would bounce the user between them forever.
    /// </summary>
    DocumentUnavailable,
}

/// <summary>
/// Next document the user must sign in the onboarding consent step.
/// <see cref="Next"/> is non-null exactly when <see cref="Outcome"/> is
/// <see cref="NextConsentStepOutcome.Document"/>.
/// <see cref="CurrentIndex"/> is 1-based progress within <see cref="TotalRequired"/>.
/// </summary>
internal sealed record NextConsentStepData(
    NextConsentStepOutcome Outcome,
    ConsentReviewDetail? Next,
    int CurrentIndex,
    int TotalRequired);

/// <summary>
/// The intake funnel in full. <see cref="IOnboardingIntake"/> carries the two members
/// consumed from outside the section; everything below it — the review queue, its detail
/// view, the consent-check clear/flag pair and the widget's next-document resolver — has
/// no consumer outside Onboarding and stays internal along with its DTOs.
/// </summary>
/// <remarks>
/// The interface survives internalisation for the usual two reasons at once: it is where
/// the <c>IOrchestrator</c> marker lives (<c>memory/architecture/orchestrator-marker.md</c>),
/// and <c>OnboardingWidgetControllerConsentsTests</c> substitutes it, which Castle
/// DynamicProxy cannot do to the <c>internal sealed</c> implementation.
/// </remarks>
internal interface IOnboardingService : IOnboardingIntake, IOrchestrator
{
    // --- Queries ---
    Task<ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default);
    Task<ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default);

    // --- Consent check mutations ---
    Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);
    Task<BulkOnboardingResult> BulkClearConsentChecksAsync(
        IReadOnlyCollection<Guid> userIds, Guid reviewerId, CancellationToken ct = default);
    Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Resolves the next required-for-Volunteers document the user still has to sign,
    /// with 1-based progress ordinals for the widget. When nothing is left to sign,
    /// self-heals a consent-suspended user who is already compliant (e.g. the required
    /// set shrank after they were suspended — no fresh signature will ever fire the
    /// restore in <c>SubmitConsentAsync</c>). See <see cref="NextConsentStepOutcome"/>
    /// for the three ways this resolves.
    /// </summary>
    Task<NextConsentStepData> GetNextUnsignedConsentAsync(
        Guid userId, CancellationToken ct = default);
}
