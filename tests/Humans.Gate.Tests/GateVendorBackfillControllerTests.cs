using Humans.Gate.Controllers;
using Humans.Gate.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Gate.Tests;

public class GateVendorBackfillControllerTests
{
    private static readonly GateVendorBackfillRow RowA =
        new("TIX-A", "Ada", Instant.FromUtc(2026, 7, 2, 9, 0), "vt-A");

    private static readonly GateVendorBackfillRow RowB =
        new("TIX-B", "Ben", Instant.FromUtc(2026, 7, 2, 9, 5), "vt-B");

    private readonly IGateService _gate = Substitute.For<IGateService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    public GateVendorBackfillControllerTests()
    {
        _gate.GetVendorCheckInBackfillAsync(Arg.Any<CancellationToken>())
            .Returns(new GateVendorBackfillSnapshot(2, 0, [RowB], [], [RowA], true));
        _gate.EnqueueVendorCheckInBackfillAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GateVendorBackfillResult(true, [RowA]));
    }

    private GateVendorBackfillAdminController BuildController()
    {
        var controller = new GateVendorBackfillAdminController(_users, _gate);

        // SetError resolves ILoggerFactory from RequestServices and reads the action
        // descriptor; RedirectToAction lazily resolves Url — stub all three.
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor(),
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        return controller;
    }

    [HumansTheory]
    [InlineData("vt-A", "vt-A")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public async Task RunOne_ForwardsOnlyTheSelectedTicket(string? postedId, string expectedId)
    {
        var controller = BuildController();

        Assert.IsType<RedirectToActionResult>(await controller.RunOne(postedId!, TestContext.Current.CancellationToken));

        await _gate.Received(1).EnqueueVendorCheckInBackfillAsync(expectedId, TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task Run_SelectsAllPendingTickets()
    {
        var controller = BuildController();

        Assert.IsType<RedirectToActionResult>(await controller.Run(TestContext.Current.CancellationToken));

        await _gate.Received(1).EnqueueVendorCheckInBackfillAsync(null, TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task Index_RendersTheServiceSnapshot()
    {
        var model = Assert.IsType<GateVendorBackfillSnapshot>(
            Assert.IsType<ViewResult>(await BuildController().Index(TestContext.Current.CancellationToken)).Model);

        Assert.Equal(["vt-B"], model.Pending.Select(r => r.VendorTicketId), StringComparer.Ordinal);
        Assert.Equal(["vt-A"], model.SentAwaitingSync.Select(r => r.VendorTicketId), StringComparer.Ordinal);
        Assert.True(model.MirrorEnabled);
    }
}
