using CsvHelper;
using Humans.Base.Csv;
using Humans.Tickets.Contracts;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Dtos;
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Tickets.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;

namespace Humans.Tickets.Tests.Controllers;

/// <summary>
/// Pins the authorization wiring of the donor-list export: the controller-wide policy admits
/// Board and TicketAdmin, and the action must override it to <see cref="PolicyNames.AdminOnly"/>
/// so buyer PII is only downloadable by admins (Docs/features/accountant-donor-list.md).
/// </summary>
public class TicketControllerTests
{
    [HumansFact]
    public async Task Reports_OrderSalesBucketsTicketTypesAndCampaignsForDisplay()
    {
        var earlier = Instant.FromUtc(2026, 1, 1, 0, 0);
        var later = earlier.Plus(Duration.FromDays(1));
        var aggregates = new TicketSalesAggregates
        {
            WeeklySales = [
                new() { WeekStart = new LocalDate(2026, 1, 5), WeekLabel = "Later" },
                new() { WeekStart = new LocalDate(2025, 12, 29), WeekLabel = "Earlier" },
            ],
            QuarterlySales = [
                new() { Year = 2026, Quarter = 2 },
                new() { Year = 2025, Quarter = 4 },
                new() { Year = 2026, Quarter = 1 },
            ],
            MonthlySales = [
                new() { MonthLabel = "2026-02", GrossRevenue = 20m },
                new() { MonthLabel = "2025-12", GrossRevenue = 5m },
                new() { MonthLabel = "2026-01", GrossRevenue = 10m },
            ],
            ByTicketType =
            [
                new() { TicketTypeName = "Early", FaceValue = 80m },
                new() { TicketTypeName = "VIP", FaceValue = 420m },
                new() { TicketTypeName = "Full", FaceValue = 200m }
            ],
            ByDiscountCampaign =
            [
                new() { CampaignTitle = "Vendor", CodesGranted = null, TotalDiscount = 900m },
                new() { CampaignTitle = "Zeta", CodesGranted = 1, TotalDiscount = 10m },
                new() { CampaignTitle = "alpha", CodesGranted = 2, TotalDiscount = 10m, CreatedAt = earlier },
                new() { CampaignTitle = "alpha", CodesGranted = 1, TotalDiscount = 10m, CreatedAt = later },
                new() { CampaignTitle = "Largest", CodesGranted = 1, TotalDiscount = 80m }
            ]
        };
        var queries = Substitute.For<ITicketManagement>();
        queries.GetSalesAggregatesAsync(Arg.Any<CancellationToken>()).Returns(aggregates);
        queries.GetCodeTrackingDataAsync(null, Arg.Any<CancellationToken>()).Returns(new CodeTrackingData
        {
            Campaigns =
            [
                new() { CampaignTitle = "Older", CreatedAt = earlier },
                new() { CampaignTitle = "Newest", CreatedAt = later },
            ]
        });
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), queries, NullLogger<TicketDashboardPageBuilder>.Instance);
        var controller = new TicketController(queries, Substitute.For<ITicketSyncService>(),
            Substitute.For<IUserParticipationBackfillService>(), dashboard, Substitute.For<IUserService>(),
            NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.SalesAggregates();

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should()
            .BeOfType<TicketSalesAggregatesViewModel>().Subject;
        model.WeeklySales.Select(week => week.WeekLabel).Should().Equal("Earlier", "Later");
        model.QuarterlySales.Select(quarter => (quarter.Year, quarter.Quarter)).Should().Equal((2025, 4), (2026, 1), (2026, 2));
        model.MonthlySales.Select(month => month.MonthLabel).Should().Equal("2025-12", "2026-01", "2026-02");
        model.ByTicketType.Select(t => t.TicketTypeName).Should().Equal("VIP", "Full", "Early");
        model.ByDiscountCampaign.Select(c => c.CampaignTitle).Should().Equal("Largest", "alpha", "alpha", "Zeta", "Vendor");
        model.ByDiscountCampaign.Where(c => string.Equals(c.CampaignTitle, "alpha", StringComparison.Ordinal))
            .Select(c => c.CodesGranted).Should().Equal(1, 2);

        var export = (await controller.ExportAccountantReport()).Should().BeOfType<FileContentResult>().Which;
        using var input = new StreamReader(new MemoryStream(export.FileContents));
        using var csv = new CsvReader(input, HumansCsv.ReadConfig());
        await csv.ReadAsync();
        csv.ReadHeader();
        var months = new List<string?>();
        while (await csv.ReadAsync()) months.Add(csv.GetField("Month"));
        months.Should().Equal("2025-12", "2026-01", "2026-02", "Total");

        var codes = await controller.Codes(null);
        codes.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TicketCodeTrackingViewModel>()
            .Which.Campaigns.Select(c => c.CampaignTitle).Should().Equal("Newest", "Older");
    }

    [HumansFact]
    public async Task WhoHasntBought_OrdersTeamFilterNamesIgnoringCase()
    {
        var queries = Substitute.For<ITicketManagement>();
        queries.GetWhoHasntBoughtAsync(null, null, null, null, 1, 25, Arg.Any<CancellationToken>())
            .Returns(new WhoHasntBoughtResult { AvailableTeams = ["Zebra", "alpha", "Bravo"] });
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), queries, NullLogger<TicketDashboardPageBuilder>.Instance);
        var controller = new TicketController(queries, Substitute.For<ITicketSyncService>(),
            Substitute.For<IUserParticipationBackfillService>(), dashboard, Substitute.For<IUserService>(),
            NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.WhoHasntBought(null);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<WhoHasntBoughtViewModel>()
            .Subject.AvailableTeams.Should().Equal("alpha", "Bravo", "Zebra");
    }

    [HumansTheory]
    [InlineData("orders")]
    [InlineData("attendees")]
    [InlineData("who-hasnt-bought")]
    [InlineData("codes")]
    [InlineData("sales")]
    [InlineData("accountant-export")]
    public async Task Admin_lists_preserve_request_cancellation(string page)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var queries = Substitute.For<ITicketManagement>();
        queries.GetOrdersPageAsync(null, "date", true, 1, 25, null, null, null, aborted.Token)
            .Returns(Task.FromException<OrdersPageResult>(new OperationCanceledException(aborted.Token)));
        queries.GetAttendeesPageAsync(null, "name", false, 1, 25, null, null, null, null, false, aborted.Token)
            .Returns(Task.FromException<AttendeesPageResult>(new OperationCanceledException(aborted.Token)));
        queries.GetWhoHasntBoughtAsync(null, null, null, null, 1, 25, aborted.Token)
            .Returns(Task.FromException<WhoHasntBoughtResult>(new OperationCanceledException(aborted.Token)));
        queries.GetCodeTrackingDataAsync(null, aborted.Token)
            .Returns(Task.FromException<CodeTrackingData>(new OperationCanceledException(aborted.Token)));
        queries.GetSalesAggregatesAsync(aborted.Token)
            .Returns(Task.FromException<TicketSalesAggregates>(new OperationCanceledException(aborted.Token)));
        var services = new ServiceCollection();
        services.AddKeyedScoped<ITicketManagement>(CachingTicketQueryService.InnerServiceKey, (_, _) => queries);
        await using var provider = services.BuildServiceProvider();
        var decorator = new CachingTicketQueryService(provider.GetRequiredService<IServiceScopeFactory>(),
            SystemClock.Instance, NullLogger<CachingTicketQueryService>.Instance);
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), decorator, NullLogger<TicketDashboardPageBuilder>.Instance);
        var controller = new TicketController(decorator, Substitute.For<ITicketSyncService>(),
            Substitute.For<IUserParticipationBackfillService>(), dashboard, Substitute.For<IUserService>(),
            NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
            },
        };
        Func<Task> read = page switch
        {
            "orders" => async () => { await controller.Orders(null); }
            ,
            "attendees" => async () => { await controller.Attendees(null); }
            ,
            "codes" => async () => { await controller.Codes(null); }
            ,
            "sales" => async () => { await controller.SalesAggregates(); }
            ,
            "accountant-export" => async () => { await controller.ExportAccountantReport(); }
            ,
            _ => async () => { await controller.WhoHasntBought(null); }
            ,
        };

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParticipationImport_RequiresActor_AndSeparatesReviewFromConfirm(bool hasActor, bool confirmed)
    {
        var actorId = Guid.NewGuid();
        var backfill = Substitute.For<IUserParticipationBackfillService>();
        backfill.BackfillFromCsvAsync(2026, "input", actorId, default, confirmed)
            .Returns(new ParticipationBackfillResult(true, "Processed", 1, confirmed ? null : "parsed rows"));
        var queries = Substitute.For<ITicketManagement>();
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), queries, NullLogger<TicketDashboardPageBuilder>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(hasActor
                ? [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())] : [], "Test"))
        };
        var controller = new TicketController(queries, Substitute.For<ITicketSyncService>(),
            backfill, dashboard, Substitute.For<IUserService>(), NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
        controller.ModelState.SetModelValue(nameof(ParticipationBackfillViewModel.CsvData), "input", "input");
        controller.ModelState[nameof(ParticipationBackfillViewModel.CsvData)]!.ValidationState =
            Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Valid;
        var model = new ParticipationBackfillViewModel { Year = 2026, CsvData = "input", Confirmed = confirmed };

        var result = await controller.ParticipationBackfill(model);

        if (!hasActor)
        {
            result.Should().BeOfType<ForbidResult>();
            backfill.ReceivedCalls().Should().BeEmpty();
            return;
        }
        await backfill.Received(1).BackfillFromCsvAsync(2026, "input", actorId, default, confirmed);
        if (confirmed)
            result.Should().BeOfType<RedirectToActionResult>();
        else
        {
            result.Should().BeOfType<ViewResult>();
            model.IsPreview.Should().BeTrue();
            model.PreviewCount.Should().Be(1);
            model.CsvData.Should().Be("parsed rows");
            controller.ModelState.ContainsKey(nameof(model.CsvData)).Should().BeFalse();
        }
    }

    [HumansFact]
    public async Task ParticipationBackfill_RequestAborted_CancelsDefaultYearRead()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var backfill = Substitute.For<IUserParticipationBackfillService>();
        backfill.GetDefaultYearAsync(aborted.Token)
            .Returns(Task.FromException<int>(new OperationCanceledException(aborted.Token)));
        var queries = Substitute.For<ITicketManagement>();
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), queries, NullLogger<TicketDashboardPageBuilder>.Instance);
        var controller = new TicketController(queries, Substitute.For<ITicketSyncService>(),
            backfill, dashboard, Substitute.For<IUserService>(), NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
            },
        };

        var act = () => controller.ParticipationBackfill();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task PersonalDataExport_RequiresAnActorAndPassesItToTheAuditedService(bool attendees, bool hasActor)
    {
        var actor = Guid.NewGuid();
        var queries = Substitute.For<ITicketManagement>();
        queries.GetAttendeeExportDataAsync(actor).Returns([]);
        queries.GetOrderExportDataAsync(actor).Returns([]);
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), queries, NullLogger<TicketDashboardPageBuilder>.Instance);
        var controller = new TicketController(queries, Substitute.For<ITicketSyncService>(),
            Substitute.For<IUserParticipationBackfillService>(), dashboard, Substitute.For<IUserService>(),
            NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(hasActor
                        ? [new Claim(ClaimTypes.NameIdentifier, actor.ToString())] : [], "Test")),
                },
            },
        };
        queries.ClearReceivedCalls();

        var result = attendees ? await controller.ExportAttendees() : await controller.ExportOrders();

        if (!hasActor)
        {
            result.Should().BeOfType<ForbidResult>();
            queries.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<FileContentResult>().Which.FileDownloadName
                .Should().Be(attendees ? "attendees-export.csv" : "orders-export.csv");
            queries.ReceivedCalls().Should().ContainSingle();
            if (attendees)
                await queries.Received(1).GetAttendeeExportDataAsync(actor);
            else
                await queries.Received(1).GetOrderExportDataAsync(actor);
        }
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersonalDataExport_OrdersCsvRows(bool attendees)
    {
        var actor = Guid.NewGuid();
        var queries = Substitute.For<ITicketManagement>();
        queries.GetAttendeeExportDataAsync(actor, Arg.Any<CancellationToken>()).Returns(
        [
            new AttendeeExportRow { AttendeeName = "Zara" },
            new AttendeeExportRow { AttendeeName = "Alice" },
            new AttendeeExportRow { AttendeeName = "Marta" },
        ]);
        queries.GetOrderExportDataAsync(actor, Arg.Any<CancellationToken>()).Returns(
        [
            new OrderExportRow { BuyerName = "Mid", PurchasedAt = Instant.FromUtc(2026, 2, 1, 0, 0) },
            new OrderExportRow { BuyerName = "Old", PurchasedAt = Instant.FromUtc(2026, 1, 1, 0, 0) },
            new OrderExportRow { BuyerName = "New", PurchasedAt = Instant.FromUtc(2026, 3, 1, 0, 0) },
        ]);
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), queries, NullLogger<TicketDashboardPageBuilder>.Instance);
        var controller = new TicketController(queries, Substitute.For<ITicketSyncService>(),
            Substitute.For<IUserParticipationBackfillService>(), dashboard, Substitute.For<IUserService>(),
            NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "Test")),
                },
            },
        };

        var result = attendees ? await controller.ExportAttendees() : await controller.ExportOrders();

        var csv = System.Text.Encoding.UTF8.GetString(result.Should().BeOfType<FileContentResult>().Which.FileContents);
        var names = attendees ? new[] { "Alice", "Marta", "Zara" } : ["New", "Mid", "Old"];
        var positions = names.Select(n => csv.IndexOf(n, StringComparison.Ordinal)).ToList();
        positions.Should().OnlyContain(p => p >= 0).And.BeInAscendingOrder();
    }

    [HumansFact]
    public void Class_admits_TicketAdmin_Board_and_Admin()
    {
        typeof(TicketController).GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Policy
            .Should().Be(TicketsPolicies.Read);
    }

    [HumansFact]
    public void ExportDonations_is_AdminOnly()
    {
        var action = typeof(TicketController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(m => string.Equals(m.Name, nameof(TicketController.ExportDonations), StringComparison.Ordinal));

        action.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("Export/Donations");
        action.GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Policy.Should().Be(PolicyNames.AdminOnly,
            because: "the class policy admits Board and TicketAdmin; the donor list carries buyer PII and is admin-only");
    }

    [HumansTheory]
    [InlineData(nameof(TicketController.Sync), TicketsPolicies.Manage)]
    [InlineData(nameof(TicketController.ExportAttendees), TicketsPolicies.Manage)]
    [InlineData(nameof(TicketController.ExportOrders), TicketsPolicies.Manage)]
    [InlineData(nameof(TicketController.ExportAccountantReport), TicketsPolicies.Manage)]
    [InlineData(nameof(TicketController.FullResync), PolicyNames.AdminOnly)]
    [InlineData(nameof(TicketController.ParticipationBackfill), PolicyNames.AdminOnly)]
    public void Action_narrows_the_class_policy_so_Board_is_denied(string actionName, string policy)
    {
        var actions = typeof(TicketController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
            .ToList();

        actions.Should().NotBeEmpty();
        actions.Should().AllSatisfy(a => a.GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Policy.Should().Be(policy));
    }

    [HumansFact]
    public void TransferAdmin_class_reads_and_decisions_manage()
    {
        typeof(TicketTransferAdminController).GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Policy
            .Should().Be(TicketsPolicies.Read);
        typeof(TicketTransferAdminController).GetMethod(nameof(TicketTransferAdminController.Decide))!
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Policy
            .Should().Be(TicketsPolicies.Manage);
    }
}
