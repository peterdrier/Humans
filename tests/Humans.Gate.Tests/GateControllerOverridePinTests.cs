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

    [HumansTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Override_ForwardsPinAndIntent_WithPrincipalAttribution(bool early, bool child)
    {
        var controller = BuildController(Pin);

        await DecideAsync(controller, overrideEarly: early, child: child, pin: Pin);

        await _gate.Received(1).RecordDecisionAsync(
            Arg.Is<GateDecisionInput>(i => i.SupervisorPin == Pin && i.OverrideEarly == early
                && i.ChildWithAdult == child), _gateAccount, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData((int)GateOverrideFailure.NotConfigured, false, "not configured", true)]
    [InlineData((int)GateOverrideFailure.InvalidPin, false, "not accepted", true)]
    [InlineData((int)GateOverrideFailure.InvalidPin, true, "not accepted", false)]
    [InlineData((int)GateOverrideFailure.LockedOut, false, "wait 60s", true)]
    public async Task Override_MapsServiceFailuresToRetryCards(
        int failureValue, bool child, string reason, bool allowOverride)
    {
        var failure = (GateOverrideFailure)failureValue;
        _gate.RecordDecisionAsync(Arg.Any<GateDecisionInput>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GateDecisionResult(GateVerdict.Unresolved, null, null, false, null, null,
                OverrideFailure: failure, RetryAfterSeconds: failure == GateOverrideFailure.LockedOut ? 60 : null));

        var card = CardOf(await DecideAsync(BuildController(Pin), overrideEarly: !child, child: child, pin: "0000"));

        card.Kind.Should().Be(GateCardKind.Amber);
        card.Reason.Should().Contain(reason);
        card.AllowSupervisorOverride.Should().Be(allowOverride);
        card.AllowChildWithAdult.Should().BeFalse();
    }
}
