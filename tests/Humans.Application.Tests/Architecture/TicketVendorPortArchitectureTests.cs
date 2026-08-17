using System.Reflection;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.Web.Extensions;

namespace Humans.Application.Tests.Architecture;

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
/// Shell's <c>TicketVendorHealthCheck</c> is the deliberate exception: a health check
/// probes the connector on purpose, and it stays in Shell because <c>Program.cs</c> names
/// each <c>IHealthCheck</c> by concrete type (design §15.6b).
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
        "Humans.Web.Health.TicketVendorHealthCheck",
    ];

    [HumansFact]
    public void OnlyTicketsAndTheHealthCheckInjectTheVendorPort()
    {
        // Anchored on a Base type, not on the port: since G5 lane 4b-2g the port is
        // declared in Humans.Tickets, which SectionAssemblies() already returns —
        // anchoring on it would silently drop Humans.Application out of the sweep.
        // It used to read typeof(CacheKeys).Assembly, which G5 lane 3a-1 moved to
        // Humans.Interfaces with its namespace preserved — that would have silently
        // dropped Humans.Application out of this sweep instead of failing.
        // DashboardService is a concrete Humans.Application service with no scheduled
        // move in phase 3.
        var applicationAssembly = typeof(Humans.Application.Services.Dashboard.DashboardService).Assembly;
        applicationAssembly.GetName().Name.Should().Be("Humans.Application",
            because: "an anchor whose type leaves this assembly would silently drop Humans.Application out of this sweep instead of failing");

        // Humans.Infrastructure was a third entry until G5 lane 5b-6 deleted it.
        var assemblies = SectionDiscoveryExtensions.SectionAssemblies()
            .Append(applicationAssembly)
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

    [HumansFact]
    public void TheTicketsLeafDoesNotNameThePortsVocabulary()
    {
        var leaf = Assembly.Load("Humans.Tickets.Contracts");
        // By assembly, not by namespace: since G5 lane 4b-2g the port sits in
        // Humans.Tickets/Contracts/ and shares the *namespace* Humans.Tickets.Contracts
        // with this leaf, so a namespace comparison would flag every leaf type.
        var portAssembly = typeof(ITicketVendorService).Assembly;

        var offenders = leaf.GetExportedTypes()
            .SelectMany(t => t.GetMethods().SelectMany(m =>
                m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
            .SelectMany(Flatten)
            .Where(t => t.Assembly == portAssembly)
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "the section maps DiscountCodeSpec/DiscountType to its own TicketDiscountCodeRequest/TicketDiscountKind at the edge; re-exporting the port's types on the leaf would put the vendor's vocabulary back in front of every consumer");

        static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            foreach (var arg in type.GetGenericArguments())
                foreach (var nested in Flatten(arg))
                    yield return nested;
        }
    }
}
