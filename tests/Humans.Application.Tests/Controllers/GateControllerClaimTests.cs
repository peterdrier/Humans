using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Application.Tests.Infrastructure;
using Humans.Web.Controllers;
using Humans.Web.Infrastructure;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Gate claim flow: per-staffer PIN throttling, claimant validation, and the
/// kiosk-scoped name search. Covers the fixes for the gate-wide throttle lockout
/// (per-target-user, not per shared device), unvalidated-claimant attribution,
/// and the route-locked /Gate/Search the kiosk picker uses instead of the
/// blocked /api/profiles/search.
/// </summary>
public class GateControllerClaimTests
{
    private const string ScannerSessionKey = "GateScannerId";

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 10, 12, 0));
    private readonly IGateService _gate = Substitute.For<IGateService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly GatePinThrottle _throttle;
    private readonly GateController _controller;
    private readonly TestSession _session = new();

    public GateControllerClaimTests()
    {
        _throttle = new GatePinThrottle(new MemoryCache(new MemoryCacheOptions()), _clock);
        _controller = new GateController(
            _gate, _users, new ConfigurationBuilder().Build(), _throttle,
            new GateVendorMirrorLedger(new MemoryCache(new MemoryCacheOptions())), _clock);

        var http = new DefaultHttpContext { Session = _session };
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
        _controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
    }

    // ── S1: per-user throttle, no gate-wide lockout ──────────────────────────
    [HumansFact]
    public async Task ClaimPin_Throttle_IsPerUser_OneStafferLockoutDoesNotBlockAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        _gate.GetPinStatusAsync(userA, Arg.Any<CancellationToken>()).Returns(new GatePinStatus(true, false));
        _gate.GetPinStatusAsync(userB, Arg.Any<CancellationToken>()).Returns(new GatePinStatus(true, false));
        _users.GetUserInfoAsync(userA, Arg.Any<CancellationToken>()).Returns(UserInfoStubHelpers.MakeUserInfo(userA));
        _users.GetUserInfoAsync(userB, Arg.Any<CancellationToken>()).Returns(UserInfoStubHelpers.MakeUserInfo(userB));
        _gate.VerifyPinAsync(userA, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _gate.VerifyPinAsync(userB, "1357", Arg.Any<CancellationToken>()).Returns(true);

        // Staffer A fat-fingers their PIN until their bucket locks (same kiosk/device).
        for (var i = 0; i < GatePinThrottle.MaxFailures; i++)
            await _controller.ClaimPin(userA, "0000", ct);

        // Staffer B on the SAME terminal must still be able to claim — A's lockout
        // is per-person, not a gate-wide freeze.
        var result = await _controller.ClaimPin(userB, "1357", ct);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(GateController.Index), redirect.ActionName);
        Assert.Equal(userB.ToString(), _session.GetString(ScannerSessionKey));
    }

    // ── S2: claimant must resolve to an active member ────────────────────────
    [HumansFact]
    public async Task ClaimPin_UnknownUserId_DoesNotEnrolOrStampSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var bogus = Guid.NewGuid();

        _gate.GetPinStatusAsync(bogus, Arg.Any<CancellationToken>()).Returns(new GatePinStatus(false, false));
        _users.GetUserInfoAsync(bogus, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        await _controller.ClaimPin(bogus, "1357", ct);

        await _gate.DidNotReceive().SetOwnPinAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(ScannerSessionKey, _session.Keys);
    }

    // ── F2: kiosk-scoped name search (replaces the route-blocked profile API) ──
    [HumansFact]
    public async Task Search_ReturnsNameMatches_ShapedForThePicker()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        _users.SearchUsersAsync("ann", PersonSearchFields.Name, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new HumanSearchResult(id, Guid.NewGuid(), "Annie", "/pic.jpg", "Name", null, null, 100) });

        var result = await _controller.Search("ann", ct);

        var json = Assert.IsType<JsonResult>(result);
        var rows = Assert.IsType<List<HumanLookupSearchResult>>(json.Value);
        var row = Assert.Single(rows);
        Assert.Equal(id, row.UserId);
        Assert.Equal("Annie", row.DisplayName);
    }

    [HumansFact]
    public async Task Search_BlankQuery_ReturnsEmpty_WithoutHittingTheSearchService()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _controller.Search("  ", ct);

        Assert.IsType<JsonResult>(result);
        await _users.DidNotReceive().SearchUsersAsync(
            Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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
