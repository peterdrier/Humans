using System.Security.Claims;
using AwesomeAssertions;
using Humans.Gate.Controllers;
using Humans.Gate.Domain;
using Humans.Gate.Models;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Gate.Tests;

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
    private readonly IUserService _users = Substitute.For<IUserService>();

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
        var controller = new GateController(_gate, _users, config, throttle, _clock, NullLogger<GateController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _gateAccount.ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private Task<IActionResult> DecideAsync(GateController c, bool overrideEarly = false, bool child = false, string? pin = null) =>
        c.Decision(Barcode, idConfirmed: false, childWithAdult: child, overrideEarly: overrideEarly,
            supervisorPin: pin, laneId: null, TestContext.Current.CancellationToken);

    private static GateScanCardViewModel CardOf(IActionResult result) =>
        Assert.IsType<GateScanCardViewModel>(Assert.IsType<PartialViewResult>(result).Model);

    [HumansTheory]
    [InlineData(null, null, true)]
    [InlineData(null, "idConfirmed", true)]
    [InlineData(true, "childWithAdult", true)]
    [InlineData(true, "overrideEarly", true)]
    [InlineData(null, null, false)]
    [InlineData(null, "idConfirmed", false)]
    public async Task Decision_MissingOrMalformedValues_DoNotRecordADefaultedVerdict(
        bool? idConfirmed, string? invalidField, bool authenticated)
    {
        var controller = BuildController(Pin);
        if (!authenticated) controller.HttpContext.User = new ClaimsPrincipal();
        if (invalidField is not null) controller.ModelState.AddModelError(invalidField, "Invalid boolean.");

        var result = await controller.Decision(Barcode, idConfirmed, childWithAdult: false,
            overrideEarly: false, supervisorPin: null, laneId: null, TestContext.Current.CancellationToken);

        if (authenticated) Assert.IsType<BadRequestResult>(result);
        else Assert.IsType<UnauthorizedResult>(result);
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Decision_ExplicitIdConfirmation_RecordsTheDecision()
    {
        var controller = BuildController(Pin);

        await controller.Decision(Barcode, idConfirmed: true, childWithAdult: false,
            overrideEarly: false, supervisorPin: null, laneId: null, TestContext.Current.CancellationToken);

        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.IdConfirmed), _gateAccount, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_WrongPin_IsRefused_AndNothingIsRecorded()
    {
        var controller = BuildController(Pin);

        var card = CardOf(await DecideAsync(controller, overrideEarly: true, pin: "0000"));

        Assert.Equal(GateCardKind.Amber, card.Kind);
        Assert.True(card.AllowSupervisorOverride); // inline retry stays available
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_CorrectPin_RecordsOverrideByTheGateAccount()
    {
        var controller = BuildController(Pin);

        await DecideAsync(controller, overrideEarly: true, pin: Pin);

        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.OverrideByUserId == _gateAccount),
            _gateAccount, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_NoConfiguredPin_FailsClosed()
    {
        var controller = BuildController(configuredPin: null);

        var card = CardOf(await DecideAsync(controller, overrideEarly: true, pin: "1111"));

        Assert.Equal(GateCardKind.Amber, card.Kind);
        Assert.Contains("not configured", card.Reason, StringComparison.OrdinalIgnoreCase);
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_Throttle_LocksOverridesOnly_PlainDecisionsKeepFlowing()
    {
        var controller = BuildController(Pin);

        // Literal 5, not GatePinThrottle.MaxFailures — a test written against the constant
        // it pins cannot notice the constant changing.
        for (var i = 0; i < 5; i++)
            await DecideAsync(controller, overrideEarly: true, pin: "0000");

        // Even the CORRECT pin is now refused (locked out)…
        var locked = CardOf(await DecideAsync(controller, overrideEarly: true, pin: Pin));
        Assert.Contains("wait", locked.Reason, StringComparison.OrdinalIgnoreCase);
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // …and the lockout does not decay early: 14 minutes in, still locked…
        _clock.Advance(Duration.FromMinutes(14));
        var stillLocked = CardOf(await DecideAsync(controller, overrideEarly: true, pin: Pin));
        Assert.Contains("wait", stillLocked.Reason, StringComparison.OrdinalIgnoreCase);

        // …but a plain (non-override) decision on the same terminal still records.
        await DecideAsync(controller);
        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.OverrideByUserId == null),
            _gateAccount, Arg.Any<CancellationToken>());

        // Past the full 15 minutes the correct PIN authorizes again.
        _clock.Advance(Duration.FromMinutes(1) + Duration.FromSeconds(1));
        await DecideAsync(controller, overrideEarly: true, pin: Pin);
        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.OverrideByUserId == _gateAccount),
            _gateAccount, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ChildWaiver_RequiresTheSamePin()
    {
        var controller = BuildController(Pin);

        var card = CardOf(await DecideAsync(controller, child: true, pin: "9999"));

        Assert.Equal(GateCardKind.Amber, card.Kind);
        Assert.False(card.AllowSupervisorOverride); // child waiver retries by re-scanning
        await _gate.DidNotReceive().RecordDecisionAsync(
            Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        await DecideAsync(controller, child: true, pin: Pin);
        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.ChildWithAdult && i.OverrideByUserId == _gateAccount),
            _gateAccount, Arg.Any<CancellationToken>());
    }
}
