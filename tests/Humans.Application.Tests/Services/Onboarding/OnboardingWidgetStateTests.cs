using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Onboarding;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Onboarding;

public class OnboardingWidgetStateTests
{
    private readonly IProfileService _profile = Substitute.For<IProfileService>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IMembershipCalculator _membership = Substitute.For<IMembershipCalculator>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IHttpContextAccessor _http = Substitute.For<IHttpContextAccessor>();
    private readonly DefaultHttpContext _httpContext = new();

    public OnboardingWidgetStateTests()
    {
        _http.HttpContext.Returns(_httpContext);
        // Session is provided by a no-op test session in the helper below.
        _httpContext.Session = new TestSession();
    }

    private OnboardingWidgetState BuildSut() =>
        new(_profile, _signups, _membership, _shiftMgmt, _http);

    [HumansFact]
    public async Task ConsentsComplete_ShortCircuitsToComplete_EvenWithoutSignup()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(true);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Complete, step);
    }

    [HumansFact]
    public async Task NoProfile_ReturnsNames()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default).Returns((Profile?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Names, step);
    }

    [HumansFact]
    public async Task ProfileButNoSignupAndNoSkip_ReturnsShifts()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId, BurnerName = "x", FirstName = "y", LastName = "z" });
        _shiftMgmt.GetActiveAsync()
            .Returns(new EventSettings { Id = eventId });
        _signups.GetActiveSignupStatusesAsync(userId, eventId)
            .Returns((new HashSet<Guid>(), new Dictionary<Guid, SignupStatus>()));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Shifts, step);
    }

    [HumansFact]
    public async Task ProfileWithSkipFlag_ReturnsConsents()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId });
        _shiftMgmt.GetActiveAsync()
            .Returns(new EventSettings { Id = eventId });
        _signups.GetActiveSignupStatusesAsync(userId, eventId)
            .Returns((new HashSet<Guid>(), new Dictionary<Guid, SignupStatus>()));
        _httpContext.Session.SetString(OnboardingWidgetState.ShiftSkipSessionKey, "true");

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [HumansFact]
    public async Task ProfileWithCurrentEventSignup_ReturnsConsents()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId });
        _shiftMgmt.GetActiveAsync()
            .Returns(new EventSettings { Id = eventId });
        _signups.GetActiveSignupStatusesAsync(userId, eventId)
            .Returns((new HashSet<Guid> { shiftId },
                      new Dictionary<Guid, SignupStatus> { [shiftId] = SignupStatus.Pending }));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
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
