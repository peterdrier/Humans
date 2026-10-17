using System.Security.Claims;
using Humans.Consent;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Constants;
using Humans.Onboarding.Controllers;
using Humans.Onboarding.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Onboarding.Tests.Controllers;

/// <summary>
/// Step 2 of the onboarding widget — covers the SignUp POST and Skip POST
/// endpoints, plus cancellation of the Shifts GET. Rendered response models
/// are exercised separately at the integration layer.
/// </summary>
public class OnboardingWidgetControllerShiftsTests
{
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileEditorService _profileEditor = Substitute.For<IProfileEditorService>();
    private readonly IShiftSignups _signups = Substitute.For<IShiftSignups>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly ISettingsService _burnSettings = Substitute.For<ISettingsService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IConsentSubmission _consents = Substitute.For<IConsentSubmission>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ConsentResource> _consentLocalizer =
        Substitute.For<IStringLocalizer<ConsentResource>>();

    private readonly IStringLocalizer<OnboardingResource> _localizer =
        Substitute.For<IStringLocalizer<OnboardingResource>>();
    private readonly DefaultHttpContext _http = new();

    public OnboardingWidgetControllerShiftsTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _consentLocalizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        _http.Session = new TestSession();
        _http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        var ctrl = new OnboardingWidgetController(_userService, _state, _profileEditor, _signups, _shiftMgmt, _burnSettings, _shiftView, _consents, _onboardingService, NodaTime.SystemClock.Instance, _localizer, _consentLocalizer);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = _http,
        };
        // Pre-set Url so RedirectToAction doesn't try to resolve IUrlHelperFactory from RequestServices.
        ctrl.Url = Substitute.For<IUrlHelper>();
        ctrl.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            _http,
            Substitute.For<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());
        return ctrl;
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shifts_Get_ShowsResolvedAccountsExistingSignup(bool mergedAlias)
    {
        var userId = Guid.NewGuid();
        var inputId = mergedAlias ? Guid.NewGuid() : userId;
        var shiftId = Guid.NewGuid();
        _userService.GetUserInfoAsync(inputId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId, State = UserState.Active }, [], [], [],
                UserFixtures.Profile(burnerName: "Burner", firstName: "First", lastName: "Last"), []));
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(BurnFixtures.Burn());
        _shiftMgmt.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UrgentShiftInfo>());
        _shiftView.GetUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => new ValueTask<ShiftUserSummary>(ShiftUserSummary.Empty(c.Arg<Guid>())));
        _shiftView.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserSummary>(ShiftFixtures.UserSummary(userId,
                [ShiftFixtures.Signup(shiftId: shiftId, status: SignupStatus.Pending)])));

        var result = await BuildSut(inputId).Shifts(ct: TestContext.Current.CancellationToken);

        var model = Assert.IsType<Humans.Onboarding.Models.ShiftsStepViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Contains(shiftId, model.UserSignupShiftIds);
        Assert.Equal(SignupStatus.Pending, model.UserSignupStatuses[shiftId]);
    }

    [HumansFact]
    public async Task Shifts_Get_StopsBrowseReadWhenRequestIsAbandoned()
    {
        var userId = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(BurnFixtures.Burn());
        _shiftView.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserSummary>(ShiftUserSummary.Empty(userId)));
        _shiftMgmt.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await request.CancelAsync();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return (IReadOnlyList<UrgentShiftInfo>)Array.Empty<UrgentShiftInfo>();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildSut(userId).Shifts(ct: request.Token));
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("localized early-entry capacity warning")]
    public async Task SignUp_Post_CallsService_AndRedirectsToConsents(string? warning)
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _signups.SignUpAsync(userId, shiftId, actorUserId: userId)
            .Returns(SignupResult.Ok(Guid.NewGuid(), warning));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignUp(shiftId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        Assert.Equal(warning, ctrl.TempData[TempDataKeys.InfoMessage]);
        await _signups.Received(1).SignUpAsync(userId, shiftId, actorUserId: userId);
    }

    [HumansFact]
    public async Task SignUp_Post_OnFailure_RedirectsToShifts_WithTempDataError()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _signups.SignUpAsync(userId, shiftId, actorUserId: userId)
            .Returns(SignupResult.Fail("nope"));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignUp(shiftId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);
        // Use the canonical TempData key so <vc:temp-data-alerts /> reads it.
        Assert.Equal("nope", ctrl.TempData[TempDataKeys.ErrorMessage]);
    }

    [HumansFact]
    public async Task SignUpRange_Post_PreservesPartialSignupWarningAcrossRedirect()
    {
        var userId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        const string warning = "localized warning naming full days omitted from signup";
        _signups.SignUpRangeAsync(userId, rotaId, -3, -1)
            .Returns(SignupResult.Ok(Guid.NewGuid(), warning));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignUpRange(rotaId, -3, -1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        Assert.Equal(warning, ctrl.TempData[TempDataKeys.InfoMessage]);
    }

    [HumansFact]
    public void Skip_Post_SetsSessionFlag_AndRedirectsToConsents()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);

        var result = ctrl.Skip();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        Assert.Equal("true", _http.Session.GetString(HttpOnboardingWidgetSessionState.ShiftSkipSessionKey));
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => _store.Keys;
        public void Clear() => _store.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public void Set(string key, byte[] value) => _store[key] = value;
        public bool TryGetValue(string key, out byte[] value)
        {
            if (_store.TryGetValue(key, out var v)) { value = v; return true; }
            value = [];
            return false;
        }
    }
}
