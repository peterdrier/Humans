using System.Reflection;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Tickets is the only door to ticketing, so swapping the vendor (expected for 2027) replaces
/// <c>Humans.TicketTailor</c> and changes nothing else. The health check probes the vendor on purpose.
/// </summary>
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
    public void TheVendorPortHasExactlyOneAdapterPlusTicketsOwnCachingDecorator()
    {
        var impls = SectionDiscoveryExtensions.SectionAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsInterface: false, IsAbstract: false }
                        && typeof(ITicketVendorService).IsAssignableFrom(t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        impls.Should().BeEquivalentTo(
            ["Humans.Tickets.Services.Stores.CachingTicketVendorService",
             "Humans.TicketTailor.Services.StubTicketVendorService",
             "Humans.TicketTailor.Services.TicketTailorService"],
            because: "the adapter section holds the one live/stub pair, so replacing the vendor is one project deleted and one added; the caching decorator lives beside the port in Tickets so the swap doesn't touch it");
    }
}
