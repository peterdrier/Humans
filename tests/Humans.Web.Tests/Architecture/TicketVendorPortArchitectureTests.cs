using System.Reflection;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Pins the shape the Tickets/TicketTailor split exists for: <b>Tickets is the
/// application's only door to ticketing</b>.
/// </summary>
/// <remarks>
/// <para>
/// A new ticket vendor is expected for 2027. At that point <c>Humans.TicketTailor</c> is
/// deleted and <c>Humans.&lt;NewVendor&gt;</c> is added, and nothing in
/// <c>Humans.Tickets</c>, in Base or in any consumer changes — but only while
/// <see cref="ITicketVendorService"/> keeps exactly the two injection sites below.
/// Campaigns used to be a third: it reached straight past Tickets into
/// <c>GenerateDiscountCodesAsync</c> because the model had not been worked out in time. It
/// now goes through <c>Humans.Tickets.Contracts.ITicketDiscountCodes</c>, and this test is
/// what stops the next section re-cutting that cow path.
/// </para>
/// <para>
/// <c>TicketVendorHealthCheck</c> is the deliberate exception: a health check probes the
/// connector on purpose. It moved from Shell into Tickets at
/// nobodies-collective/Humans#1075 — the check registers itself through
/// <c>SectionHealthChecks : ISectionHealthChecks</c>, so <c>Program.cs</c> no longer names
/// it and the owning section holds its own probe.
/// </para>
/// </remarks>
public class TicketVendorPortArchitectureTests
{
    private static readonly string[] AllowedInjectionSites =
    [
        "Humans.Tickets.Services.TicketVendorGateway",
        "Humans.Tickets.Services.TicketSyncService",
        "Humans.Tickets.Services.TicketTransferService",
        "Humans.Tickets.Models.TicketDashboardPageBuilder",
        "Humans.Tickets.Health.TicketVendorHealthCheck",
    ];

    [HumansFact]
    public void OnlyTicketsAndTheHealthCheckInjectTheVendorPort()
    {
        // Humans.Infrastructure was an entry until G5 lane 5b-6 deleted it, and
        // Humans.Application until lane 5c emptied it of every type.
        var assemblies = SectionDiscoveryExtensions.SectionAssemblies()
            .Append(Assembly.Load("Humans.Web"))
            .Distinct()
            .ToList();

        assemblies.Should().HaveCountGreaterThan(
            20, "an anchor that resolves to few assemblies makes this sweep pass vacuously");

        var injectors = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ITicketVendorService))))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        injectors.Where(n => !AllowedInjectionSites.Contains(n, StringComparer.Ordinal))
            .Should().BeEmpty(
                because: "everything else asks Tickets, through Humans.Tickets.Contracts — the port has one adapter and one owning section, which is what makes the 2027 vendor swap a project delete");
    }

    [HumansFact]
    public void TheVendorPortHasExactlyOneAdapter()
    {
        var impls = SectionDiscoveryExtensions.SectionAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsInterface: false, IsAbstract: false }
                        && typeof(ITicketVendorService).IsAssignableFrom(t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        impls.Should().BeEquivalentTo(
            ["Humans.TicketTailor.Services.StubTicketVendorService",
             "Humans.TicketTailor.Services.TicketTailorService"],
            because: "the port's implementations all live in the adapter section, so replacing the vendor is one project deleted and one added");
    }
}
