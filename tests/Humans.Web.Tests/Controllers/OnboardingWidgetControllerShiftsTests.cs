using System.Security.Claims;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Constants;
using Humans.Web.Controllers;
using Humans.Web.Services.Onboarding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Step 2 of the onboarding widget — covers the SignUp POST and Skip POST
/// endpoints. The Shifts GET is exercised separately at the integration layer
/// because its response model is built from <see cref="IShiftManagementService"/>
/// and does not affect step routing.
/// </summary>
public class OnboardingWidgetControllerShiftsTests
{
    private readonly UserManager<User> _userManager;
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileEditorService _profileEditor = Substitute.For<IProfileEditorService>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<SharedResource> _localizer =
        Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly DefaultHttpContext _http = new();

    public OnboardingWidgetControllerShiftsTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        var user = new User { Id = userId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        _http.Session = new TestSession();
        _http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        // SetError on HumansControllerBase resolves ILoggerFactory from RequestServices.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _http.RequestServices = services.BuildServiceProvider();
        var ctrl = new OnboardingWidgetController(_userService, _state, _profileEditor, _signups, _shiftMgmt, _shiftView, _consents, _onboardingService, _localizer);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = _http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ActionName = "Test" },
        };
        // Pre-set Url so RedirectToAction doesn't try to resolve IUrlHelperFactory from RequestServices.
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    [HumansFact]
    public async Task SignUp_Post_CallsService_AndRedirectsToConsents()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _signups.SignUpAsync(userId, shiftId, actorUserId: userId)
            .Returns(SignupResult.Ok(new ShiftSignup()));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignUp(shiftId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
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
        ctrl.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            _http,
            Substitute.For<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());

        var result = await ctrl.SignUp(shiftId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);
        // Use the canonical TempData key so <vc:temp-data-alerts /> reads it.
        Assert.Equal("nope", ctrl.TempData[TempDataKeys.ErrorMessage]);
    }

    [HumansFact]
    public void Skip_Post_SetsSessionFlag_AndRedirectsToConsents()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);

        var result = ctrl.Skip(CancellationToken.None);

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
