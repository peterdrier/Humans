using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Gate.Controllers;
using Humans.Gate.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Gate.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Gate
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/GateArchitectureTests.cs</c>, whose four
/// tests pinned the Application/Infrastructure split the section no longer has — that
/// <c>GateService</c> sat in <c>Humans.Application.Services.Gate</c>, took no DbContext, and
/// reached its tables through <c>IGateRepository</c>. One assembly with one internal surface
/// subsumes the first three; the cross-section read assertion is kept below because it is
/// about Tickets, not about where Gate lives.
/// </remarks>
public class GateArchitectureTests
{
    [HumansFact]
    public void GateService_ReadsTicketsViaReadInterface()
    {
        var paramTypes = typeof(GateService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketServiceRead));

        // The full ticket service is internal to Humans.Tickets since that section's G5 move,
        // so it can no longer be named here — assert on the namespace instead, which also
        // catches a future Tickets type that is public but not on the contracts leaf.
        paramTypes
            .Where(t => t.Namespace?.StartsWith("Humans.Tickets", StringComparison.Ordinal) == true)
            .Should().OnlyContain(t => t.Namespace == "Humans.Tickets.Contracts",
                because: "cross-section ticket reads must use the contracts leaf (section-read-write-split / HUM0032)");
    }

    [HumansFact]
    public void ServiceImplementsTheUserLifecycleContracts()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(GateService))
            .Should().BeTrue(
                because: "Gate owns gate_scan_events and gate_staff_pins (user-scoped); it must "
                       + "contribute to the GDPR Article 15 export");
        typeof(IUserMerge).IsAssignableFrom(typeof(GateService))
            .Should().BeTrue(
                because: "GuestUserId / ScannedByUserId / OverrideByUserId are re-pointed on account merge");
    }

    [HumansFact]
    public void WriteAndAdminActions_NeverRideOnTheClassLevelReadPolicy()
    {
        // The kiosk read surface is the class-level ScannerAccess policy…
        typeof(GateController).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(PolicyNames.ScannerAccess);

        // …and every POST must carry its own stricter policy: GateAdmit for the
        // terminal writes, TicketAdminOrAdmin for the admin pages. A new action
        // that forgets its policy would silently ride on the read-only gate.
        var posts = typeof(GateController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .ToList();

        posts.Should().NotBeEmpty();
        foreach (var post in posts)
        {
            var expected = post.Name is nameof(GateController.Admin)
                    or nameof(GateController.SetStaffPin) or nameof(GateController.ResetStaffPin)
                ? PolicyNames.TicketAdminOrAdmin
                : PolicyNames.GateAdmit;
            post.GetCustomAttribute<AuthorizeAttribute>()?.Policy.Should().Be(expected,
                because: $"POST {post.Name} must not inherit only the class-level read policy");
        }

        // The one-off vendor backfill page is admin-only at the class level.
        typeof(GateVendorBackfillAdminController).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(PolicyNames.AdminOnly);
    }

    [HumansFact]
    public void SectionRegistersTheUserMergeForwarder()
    {
        // When two accounts are merged, Gate has to move its scan rows over to the
        // surviving user. That only happens if this registration is present — drop it
        // and the rows silently keep pointing at the account that went away.
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserMerge));
    }
}
