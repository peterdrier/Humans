using Humans.Tickets.Contracts;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
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

    [HumansFact]
    public void Class_admits_TicketAdmin_Board_and_Admin()
    {
        typeof(TicketController).GetCustomAttribute<AuthorizeAttribute>()!.Policy
            .Should().Be(PolicyNames.TicketAdminBoardOrAdmin);
    }

    [HumansFact]
    public void ExportDonations_is_AdminOnly()
    {
        var action = typeof(TicketController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(m => string.Equals(m.Name, nameof(TicketController.ExportDonations), StringComparison.Ordinal));

        action.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("Export/Donations");
        action.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(PolicyNames.AdminOnly,
            because: "the class policy admits Board and TicketAdmin; the donor list carries buyer PII and is admin-only");
    }

    [HumansTheory]
    [InlineData(nameof(TicketController.Sync), PolicyNames.TicketAdminOrAdmin)]
    [InlineData(nameof(TicketController.ExportAttendees), PolicyNames.TicketAdminOrAdmin)]
    [InlineData(nameof(TicketController.ExportOrders), PolicyNames.TicketAdminOrAdmin)]
    [InlineData(nameof(TicketController.ExportAccountantReport), PolicyNames.TicketAdminOrAdmin)]
    [InlineData(nameof(TicketController.FullResync), PolicyNames.AdminOnly)]
    [InlineData(nameof(TicketController.ParticipationBackfill), PolicyNames.AdminOnly)]
    public void Action_narrows_the_class_policy_so_Board_is_denied(string actionName, string policy)
    {
        var actions = typeof(TicketController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
            .ToList();

        actions.Should().NotBeEmpty();
        actions.Should().AllSatisfy(a => a.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(policy));
    }

    [HumansFact]
    public void TransferAdmin_class_admits_TicketAdmin_and_Admin_only()
    {
        typeof(TicketTransferAdminController).GetCustomAttribute<AuthorizeAttribute>()!.Policy
            .Should().Be(PolicyNames.TicketAdminOrAdmin);
    }
}
