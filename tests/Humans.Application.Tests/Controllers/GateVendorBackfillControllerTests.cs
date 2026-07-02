using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Jobs;
using Humans.Web.Controllers;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// The vendor check-in backfill page's double-post guards: a just-sent ticket is
/// excluded from both the single test send and the bulk send until the ticket sync
/// confirms it (TicketTailor check-ins double-record on repeat), and nothing is
/// enqueued while <c>Gate:VendorMirrorEnabled</c> is off.
/// </summary>
public class GateVendorBackfillControllerTests
{
    private static readonly GateVendorBackfillRow RowA =
        new("TIX-A", "Ada", Instant.FromUtc(2026, 7, 2, 9, 0), "vt-A");

    private static readonly GateVendorBackfillRow RowB =
        new("TIX-B", "Ben", Instant.FromUtc(2026, 7, 2, 9, 5), "vt-B");

    private readonly IGateService _gate = Substitute.For<IGateService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly GateVendorMirrorLedger _ledger = new(new MemoryCache(new MemoryCacheOptions()));

    public GateVendorBackfillControllerTests()
    {
        _gate.GetVendorCheckInBackfillAsync(Arg.Any<CancellationToken>())
            .Returns(new GateVendorBackfillSnapshot(2, 0, [RowA, RowB], []));
    }

    private GateVendorBackfillAdminController BuildController(bool mirrorEnabled = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Gate:VendorMirrorEnabled"] = mirrorEnabled ? "true" : null,
            }).Build();
        var controller = new GateVendorBackfillAdminController(_users, _gate, config, _ledger, _jobs);

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

    private string[] EnqueuedVendorIds() =>
        _jobs.ReceivedCalls()
            .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IBackgroundJobClient.Create), StringComparison.Ordinal))
            .Select(c => (string)((Job)c.GetArguments()[0]!).Args[0]!)
            .ToArray();

    [HumansFact]
    public async Task RunOne_EnqueuesThatTicket_AndMarksItSent()
    {
        var controller = BuildController();

        await controller.RunOne("vt-A", TestContext.Current.CancellationToken);

        Assert.Equal(["vt-A"], EnqueuedVendorIds());
        Assert.True(_ledger.WasSent("vt-A"));
        Assert.False(_ledger.WasSent("vt-B"));
    }

    [HumansFact]
    public async Task RunOne_PreservesTheOriginalAdmitTime()
    {
        var controller = BuildController();

        await controller.RunOne("vt-A", TestContext.Current.CancellationToken);

        // ExecuteBackfillAsync(vendorTicketId, admittedAtUnixSeconds, ct) — the vendor
        // check-in must carry the gate admit time, not the moment the admin clicked Send.
        var job = (Job)_jobs.ReceivedCalls().Single(c =>
            string.Equals(c.GetMethodInfo().Name, nameof(IBackgroundJobClient.Create), StringComparison.Ordinal))
            .GetArguments()[0]!;
        Assert.Equal(nameof(GateVendorCheckInJob.ExecuteBackfillAsync), job.Method.Name);
        Assert.Equal(RowA.AdmittedAt.ToUnixTimeSeconds(), (long)job.Args[1]!);
    }

    [HumansFact]
    public async Task Run_AfterRunOne_ExcludesTheTestTicket()
    {
        var controller = BuildController();
        await controller.RunOne("vt-A", TestContext.Current.CancellationToken);

        // The vendor record now exists but the local sync hasn't run — RowA is still in
        // the service's Pending. The bulk send must not re-post it.
        await controller.Run(TestContext.Current.CancellationToken);

        Assert.Equal(["vt-A", "vt-B"], EnqueuedVendorIds());
    }

    [HumansFact]
    public async Task RunOne_ForAnAlreadySentTicket_IsRefused()
    {
        var controller = BuildController();
        await controller.RunOne("vt-A", TestContext.Current.CancellationToken);

        await controller.RunOne("vt-A", TestContext.Current.CancellationToken);

        Assert.Equal(["vt-A"], EnqueuedVendorIds()); // second attempt enqueued nothing
    }

    [HumansFact]
    public async Task MirrorDisabled_NothingIsEnqueued()
    {
        var controller = BuildController(mirrorEnabled: false);

        await controller.RunOne("vt-A", TestContext.Current.CancellationToken);
        await controller.Run(TestContext.Current.CancellationToken);

        Assert.Empty(EnqueuedVendorIds());
        Assert.False(_ledger.WasSent("vt-A"));
    }

    [HumansFact]
    public void TryMarkSent_ClaimsAnIdExactlyOnce()
    {
        // The atomic claim is the double-post guard: of two overlapping requests
        // (double-click / second admin), exactly one may enqueue a given ticket.
        Assert.True(_ledger.TryMarkSent("vt-X"));
        Assert.False(_ledger.TryMarkSent("vt-X"));
        Assert.True(_ledger.WasSent("vt-X"));
    }

    [HumansFact]
    public async Task Index_SplitsPendingFromSentAwaitingSync()
    {
        var controller = BuildController();
        await controller.RunOne("vt-A", TestContext.Current.CancellationToken);

        var model = Assert.IsType<GateVendorBackfillViewModel>(
            Assert.IsType<ViewResult>(await controller.Index(TestContext.Current.CancellationToken)).Model);

        Assert.Equal(["vt-B"], model.Pending.Select(r => r.VendorTicketId));
        Assert.Equal(["vt-A"], model.SentAwaitingSync.Select(r => r.VendorTicketId));
    }
}
