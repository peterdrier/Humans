using System.Security.Claims;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers;
using Humans.Web.Models.OnboardingWidget;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Step 3 of the onboarding widget — Consents GET (renders the next unsigned
/// required document inline so the user reads what they're agreeing to) and
/// SignConsent POST (routes a single signature through
/// <see cref="IConsentService.SubmitConsentAsync"/> and redirects back so the
/// dispatcher can re-evaluate the step).
/// </summary>
public class OnboardingWidgetControllerConsentsTests
{
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileService _profile = Substitute.For<IProfileService>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly DefaultHttpContext _http = new();

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        _http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
            "test"));
        var ctrl = new OnboardingWidgetController(_state, _profile, _signups, _shiftMgmt, _consents);
        ctrl.ControllerContext = new ControllerContext { HttpContext = _http };
        ctrl.TempData = new TempDataDictionary(_http, Substitute.For<ITempDataProvider>());
        return ctrl;
    }

    [HumansFact]
    public async Task SignConsent_Post_CallsConsentService_AndRedirectsThroughIndexDispatcher()
    {
        // Routing back through Index lets the dispatcher send the user Home
        // when this was the final required consent — instead of stranding
        // them on the signed-documents page.
        var userId = Guid.NewGuid();
        var docVersionId = Guid.NewGuid();
        _consents.SubmitConsentAsync(
                userId, docVersionId, true,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(Success: true));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignConsent(docVersionId, explicitConsent: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        await _consents.Received(1).SubmitConsentAsync(
            userId, docVersionId, true,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SignConsent_Post_OnFailure_RedirectsToIndex_WithTempDataError()
    {
        // Failure path also goes through Index so the dispatcher applies; the
        // dispatcher will route back to Consents (or wherever appropriate)
        // and TempData["Error"] survives the redirect.
        var userId = Guid.NewGuid();
        var docVersionId = Guid.NewGuid();
        _consents.SubmitConsentAsync(
                userId, docVersionId, true,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(Success: false, ErrorKey: "AlreadyConsented"));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignConsent(docVersionId, explicitConsent: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        Assert.Equal("AlreadyConsented", ctrl.TempData["Error"]);
    }

    [HumansFact]
    public async Task SignConsent_Post_WithoutCheckbox_RedirectsToConsents_WithError_AndDoesNotCallService()
    {
        // The checkbox is the legal "explicit consent" gesture — submitting
        // without it is a user error, not a service call. Route back to the
        // consent page with an error message; never invoke the consent service.
        var userId = Guid.NewGuid();
        var docVersionId = Guid.NewGuid();
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignConsent(docVersionId, explicitConsent: false, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        Assert.Equal("MustCheck", ctrl.TempData["Error"]);
        await _consents.DidNotReceive().SubmitConsentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Consents_Get_RendersFirstUnsignedDocumentContent()
    {
        // The widget shows the next unsigned doc inline (full content) so users
        // can read what they're agreeing to — not just a "Read the document"
        // link they might skip.
        var userId = Guid.NewGuid();
        var signedId = Guid.NewGuid();
        var unsignedId = Guid.NewGuid();
        IReadOnlyList<RequiredConsentRow> rows = new List<RequiredConsentRow>
        {
            new(signedId, "Code of Conduct", Signed: true),
            new(unsignedId, "Privacy Policy", Signed: false),
        };
        _consents.GetRequiredConsentRowsForUserAsync(
                userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(rows);
        var version = new DocumentVersion
        {
            Id = unsignedId,
            VersionNumber = "1.2",
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "# Política", ["en"] = "# Policy" },
            ChangesSummary = "Updated section 4",
            LegalDocument = new LegalDocument { Name = "Privacy Policy" },
        };
        _consents.GetConsentReviewDetailAsync(unsignedId, userId, Arg.Any<CancellationToken>())
            .Returns((version, (ConsentRecord?)null, (string?)null));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ConsentsStepViewModel>(view.Model);
        Assert.Equal(unsignedId, vm.DocumentVersionId);
        Assert.Equal("Privacy Policy", vm.DocumentName);
        Assert.Equal("1.2", vm.VersionNumber);
        Assert.Equal("Updated section 4", vm.ChangesSummary);
        Assert.Equal(2, vm.CurrentIndex);   // signed (1), unsigned (2)
        Assert.Equal(2, vm.TotalRequired);
        Assert.Equal("# Policy", vm.Content["en"]);
    }

    [HumansFact]
    public async Task Consents_Get_AllSigned_RedirectsThroughIndexDispatcher()
    {
        var userId = Guid.NewGuid();
        IReadOnlyList<RequiredConsentRow> rows = new List<RequiredConsentRow>
        {
            new(Guid.NewGuid(), "Code of Conduct", Signed: true),
        };
        _consents.GetRequiredConsentRowsForUserAsync(
                userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(rows);
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
    }
}
