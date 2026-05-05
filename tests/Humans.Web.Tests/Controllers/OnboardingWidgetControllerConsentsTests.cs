using System.Security.Claims;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
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
/// Step 3 of the onboarding widget — Consents GET (renders required-document
/// rows from <see cref="IConsentService"/>) and SignConsent POST (routes a
/// single signature through <see cref="IConsentService.SubmitConsentAsync"/>
/// and redirects back so the dispatcher can re-evaluate the step).
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

        var result = await ctrl.SignConsent(docVersionId, CancellationToken.None);

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

        var result = await ctrl.SignConsent(docVersionId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        Assert.Equal("AlreadyConsented", ctrl.TempData["Error"]);
    }

    [HumansFact]
    public async Task Consents_Get_ReadsRowsForVolunteersTeam_AndReturnsView()
    {
        var userId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        IReadOnlyList<RequiredConsentRow> rows = new List<RequiredConsentRow>
        {
            new(docId, "Code of Conduct", Signed: false),
        };
        _consents.GetRequiredConsentRowsForUserAsync(
                userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(rows);
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ConsentsStepViewModel>(view.Model);
        Assert.Single(vm.RequiredConsents);
        Assert.Equal(docId, vm.RequiredConsents[0].DocumentVersionId);
        Assert.Equal("Code of Conduct", vm.RequiredConsents[0].Title);
        Assert.False(vm.RequiredConsents[0].Signed);
    }
}
