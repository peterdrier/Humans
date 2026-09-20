using Hangfire;
using AwesomeAssertions;
using Humans.Gate.Controllers;
using Humans.Gate.Models;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Gate.Tests;

/// <summary>
/// Gate settings (general-entry instant, minor age threshold) moved to /Settings#gate
/// (peterdrier/Humans#1634) — Staff PINs stay on <c>/Gate/Admin</c>, so the save action
/// redirects to the tab rather than back to that page.
/// </summary>
public sealed class GateControllerAdminSettingsTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 10, 12, 0));
    private readonly IGateService _gate = Substitute.For<IGateService>();
    private readonly GateController _controller;

    public GateControllerAdminSettingsTests()
    {
        _controller = new GateController(
            _gate, Substitute.For<IUserServiceRead>(), new ConfigurationBuilder().Build(),
            new GatePinThrottle(new MemoryCache(new MemoryCacheOptions()), _clock),
            new GateVendorMirrorLedger(new MemoryCache(new MemoryCacheOptions())),
            Substitute.For<IBackgroundJobClient>(), _clock);

        var http = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
        _controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
    }

    [HumansFact]
    public async Task Admin_Post_ValidModel_RedirectsToTheSettingsPageGateTab()
    {
        var model = new GateSettingsViewModel("2026-07-06T10:00:00Z", 16);

        var result = await _controller.Admin(model, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#gate");
        await _gate.Received(1).SaveSettingsAsync(Arg.Any<GateSettingsDto>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Admin_Post_InvalidInstant_RedirectsToTheSettingsPageGateTabWithoutSaving()
    {
        var model = new GateSettingsViewModel("not-an-instant", 16);

        var result = await _controller.Admin(model, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#gate");
        await _gate.DidNotReceive().SaveSettingsAsync(Arg.Any<GateSettingsDto>(), Arg.Any<CancellationToken>());
    }
}
