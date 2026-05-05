using System.Security.Claims;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Onboarding;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileService _profile = Substitute.For<IProfileService>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly DefaultHttpContext _http = new();

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        _http.Session = new TestSession();
        _http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
            "test"));
        var ctrl = new OnboardingWidgetController(_state, _profile, _signups, _shiftMgmt);
        ctrl.ControllerContext = new ControllerContext { HttpContext = _http };
        return ctrl;
    }

    [HumansFact]
    public async Task SignUp_Post_CallsService_AndRedirectsToConsents()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _signups.SignUpAsync(userId, shiftId, userId, false)
            .Returns(SignupResult.Ok(new ShiftSignup()));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignUp(shiftId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        await _signups.Received(1).SignUpAsync(userId, shiftId, userId, false);
    }

    [HumansFact]
    public async Task SignUp_Post_OnFailure_RedirectsToShifts_WithTempDataError()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _signups.SignUpAsync(userId, shiftId, userId, false)
            .Returns(SignupResult.Fail("nope"));
        var ctrl = BuildSut(userId);
        ctrl.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            _http,
            Substitute.For<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());

        var result = await ctrl.SignUp(shiftId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);
        Assert.Equal("nope", ctrl.TempData["Error"]);
    }

    [HumansFact]
    public void Skip_Post_SetsSessionFlag_AndRedirectsToConsents()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);

        var result = ctrl.Skip(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        Assert.Equal("true", _http.Session.GetString(OnboardingWidgetState.ShiftSkipSessionKey));
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
            value = Array.Empty<byte>();
            return false;
        }
    }
}
