using System.Security.Claims;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Infrastructure;
using Humans.Web.Models.Gate;
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
/// The shared supervisor override PIN on <c>/Gate/Decision</c>: overrides are refused
/// without the correct <c>Gate:SupervisorPin</c> (including when the key is unset — fail
/// closed), authorized overrides record the gate account, and the terminal-wide throttle
/// locks only the override path — plain decisions keep flowing.
/// </summary>
public class GateControllerOverridePinTests
{
    private const string Barcode = "TIX-1";
    private const string Pin = "2468";

    private readonly Guid _gateAccount = Guid.NewGuid();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 7, 2, 12, 0));
    private readonly IGateService _gate = Substitute.For<IGateService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();

    public GateControllerOverridePinTests()
    {
        _gate.RecordDecisionAsync(Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GateDecisionResult(GateVerdict.AdmittedEarlyOverride, "Guest", null,
                IsEarly: true, null, null));
    }

    private GateController BuildController(string? configuredPin)
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase);
        if (configuredPin is not null)
            settings["Gate:SupervisorPin"] = configuredPin;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var throttle = new GatePinThrottle(new MemoryCache(new MemoryCacheOptions()), _clock);
        var mirrorLedger = new GateVendorMirrorLedger(new MemoryCache(new MemoryCacheOptions()));
        var controller = new GateController(_gate, _users, config, throttle, mirrorLedger, _clock);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _gateAccount.ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private Task<IActionResult> Decide(GateController c, bool overrideEarly = false, bool child = false, string? pin = null) =>
        c.Decision(Barcode, idConfirmed: false, childWithAdult: child, overrideEarly: overrideEarly,
            supervisorPin: pin, laneId: null, TestContext.Current.CancellationToken);

    private static GateScanCardViewModel CardOf(IActionResult result) =>
        Assert.IsType<GateScanCardViewModel>(Assert.IsType<PartialViewResult>(result).Model);

    [HumansFact]
    public async Task Override_WrongPin_IsRefused_AndNothingIsRecorded()
    {
        var controller = BuildController(Pin);

        var card = CardOf(await Decide(controller, overrideEarly: true, pin: "0000"));

        Assert.Equal(GateCardKind.Amber, card.Kind);
        Assert.True(card.AllowSupervisorOverride); // inline retry stays available
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_CorrectPin_RecordsOverrideByTheGateAccount()
    {
        var controller = BuildController(Pin);

        await Decide(controller, overrideEarly: true, pin: Pin);

        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.OverrideByUserId == _gateAccount),
            _gateAccount, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_NoConfiguredPin_FailsClosed()
    {
        var controller = BuildController(configuredPin: null);

        var card = CardOf(await Decide(controller, overrideEarly: true, pin: "1111"));

        Assert.Equal(GateCardKind.Amber, card.Kind);
        Assert.Contains("not configured", card.Reason, StringComparison.OrdinalIgnoreCase);
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_Throttle_LocksOverridesOnly_PlainDecisionsKeepFlowing()
    {
        var controller = BuildController(Pin);

        for (var i = 0; i < GatePinThrottle.MaxFailures; i++)
            await Decide(controller, overrideEarly: true, pin: "0000");

        // Even the CORRECT pin is now refused (locked out)…
        var locked = CardOf(await Decide(controller, overrideEarly: true, pin: Pin));
        Assert.Contains("wait", locked.Reason, StringComparison.OrdinalIgnoreCase);
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // …but a plain (non-override) decision on the same terminal still records.
        await Decide(controller);
        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.OverrideByUserId == null),
            _gateAccount, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ChildWaiver_RequiresTheSamePin()
    {
        var controller = BuildController(Pin);

        var card = CardOf(await Decide(controller, child: true, pin: "9999"));

        Assert.Equal(GateCardKind.Amber, card.Kind);
        Assert.False(card.AllowSupervisorOverride); // child waiver retries by re-scanning
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        await Decide(controller, child: true, pin: Pin);
        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.ChildWithAdult && i.OverrideByUserId == _gateAccount),
            _gateAccount, Arg.Any<CancellationToken>());
    }
}
