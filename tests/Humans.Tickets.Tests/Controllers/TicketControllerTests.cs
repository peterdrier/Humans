using Humans.Tickets.Contracts;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Tickets.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public async Task ParticipationBackfill_RequestAborted_CancelsDefaultYearRead()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var backfill = Substitute.For<IUserParticipationBackfillService>();
        backfill.GetDefaultYearAsync(aborted.Token)
            .Returns(Task.FromException<int>(new OperationCanceledException(aborted.Token)));
        var queries = Substitute.For<ITicketService>();
        var dashboard = new TicketDashboardPageBuilder(Substitute.For<ITicketVendorService>(),
            Options.Create(new TicketVendorSettings()), queries, NullLogger<TicketDashboardPageBuilder>.Instance);
        var controller = new TicketController(queries, Substitute.For<ITicketSyncService>(),
            backfill, dashboard, Substitute.For<IUserServiceRead>(), NullLogger<TicketController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
            },
        };

        var act = () => controller.ParticipationBackfill();

        await act.Should().ThrowAsync<OperationCanceledException>();
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
