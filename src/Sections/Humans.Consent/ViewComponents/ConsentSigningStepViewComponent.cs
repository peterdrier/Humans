using Humans.Consent.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Consent.ViewComponents;

/// <summary>
/// Consent's "sign this document" step for a host page that walks a member through several documents
/// (the onboarding widget). The form posts to <c>/Consent/Submit</c>, which lands back on the host's return URL.
/// </summary>
public sealed class ConsentSigningStepViewComponent : ViewComponent
{
    /// <summary>Renders the signing step for <paramref name="document"/>.</summary>
    /// <param name="document">The unsigned document version to show.</param>
    /// <param name="currentIndex">1-based position of <paramref name="document"/> in the host's sequence.</param>
    /// <param name="totalRequired">Length of the host's sequence.</param>
    /// <param name="returnUrl">Local URL the member returns to after submitting.</param>
    public IViewComponentResult Invoke(ConsentReviewDetail document, int currentIndex, int totalRequired, string returnUrl) =>
        View(new ConsentSigningStepViewModel(document, currentIndex, totalRequired, returnUrl));
}

internal sealed record ConsentSigningStepViewModel(
    ConsentReviewDetail Document,
    int CurrentIndex,
    int TotalRequired,
    string ReturnUrl);
