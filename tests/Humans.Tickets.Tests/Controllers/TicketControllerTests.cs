using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Tickets.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.Tests.Controllers;

/// <summary>
/// Pins the authorization wiring of the donor-list export: the controller-wide policy admits
/// Board and TicketAdmin, and the action must override it to <see cref="PolicyNames.AdminOnly"/>
/// so buyer PII is only downloadable by admins (Docs/features/accountant-donor-list.md).
/// </summary>
public class TicketControllerTests
{
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
}
