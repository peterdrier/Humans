using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.TicketTailor.Services;

namespace Humans.TicketTailor.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the connector boundary for the Ticket Tailor
/// integration (issue #555 — §15 Part 1). <c>ITicketVendorService</c> is the port and
/// lives under <c>Humans.Tickets/Contracts/</c>, the section that owns ticketing; the two
/// adapters live in this section, which references <c>Humans.Tickets</c> directly
/// (nobodies-collective/Humans#866, G5 lane 4b-2g — it used to sit in
/// <c>Humans.Application</c>). The interface must never leak HTTP-client or vendor-SDK types across
/// the boundary — its entire signature set (parameters, return types) must be expressible
/// in port terms (the port's own DTOs, primitives, NodaTime, BCL collections).
///
/// <para>
/// This is what makes the 2027 vendor swap a project delete: keep the port free of
/// vendor vocabulary and <c>Humans.&lt;NewVendor&gt;</c> drops in behind it. The companion
/// check on the other side — that only <c>Humans.Tickets</c> and Shell's health check
/// <em>inject</em> the port — is
/// <c>Humans.Application.Tests/Architecture/TicketVendorPortArchitectureTests</c>, which
/// needs the whole section graph and so cannot live here.
/// </para>
/// </summary>
public class TicketVendorArchitectureTests
{
    // Namespaces that indicate an HTTP-client or vendor-SDK type leaking into
    // the Application-layer interface. Matching is prefix-based; add more
    // here if a new vendor library shows up.
    private const string PortNamespace = "Humans.Tickets.Contracts";

    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Net.Http",
        "TicketTailor",
        "Humans.Infrastructure",
    ];

    [HumansFact]
    public void ITicketVendorService_LivesUnderTheTicketsSectionContracts()
    {
        typeof(ITicketVendorService).Namespace
            .Should().Be(PortNamespace,
                because: "the vendor-agnostic port is public surface of the section that owns ticketing, so it sits under Humans.Tickets/Contracts/ where HUM0034 expects a section's public types (nobodies-collective/Humans#866, G5 lane 4b-2g)");
    }

    [HumansFact]
    public void ITicketVendorService_IsDeclaredInTheTicketsAssembly()
    {
        typeof(ITicketVendorService).Assembly.GetName().Name
            .Should().Be("Humans.Tickets",
                because: "the port is compiled into the owning section, not onto the Humans.Tickets.Contracts leaf — no Base consumer names it, and the leaf must stay free of the vendor's vocabulary");
    }

    [HumansFact]
    public void NeitherThePortsAssemblyNorBaseReferencesTheAdapterSection()
    {
        // Two anchors on purpose. The adapter half follows the port (now Humans.Tickets);
        // the Humans.Application half is a layering claim about Base that never had
        // anything to do with where the port lives, so it stays anchored on Base — the
        // port's own assembly references Humans.Infrastructure by design (G5 lane 4b-2g).
        var portAssembly = typeof(ITicketVendorService).Assembly;
        var baseAssembly = typeof(Humans.Application.CacheKeys).Assembly;

        portAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Should().NotContain(
                name => name.StartsWith("Humans.TicketTailor", StringComparison.Ordinal),
                because: "the port's owning section must not reference the adapter section; the dependency runs the other way, which is what lets the adapter be deleted for the 2027 vendor");

        baseAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Should().NotContain(
                name => name.StartsWith("Humans.Infrastructure", StringComparison.Ordinal),
                because: "Humans.Application must not depend on Humans.Infrastructure — the connector pattern inverts this dependency");
    }

    [HumansFact]
    public void ITicketVendorService_ExposesNoForbiddenTypesInSignatures()
    {
        var offenders = new List<string>();

        foreach (var method in typeof(ITicketVendorService).GetMethods())
        {
            CheckType(method.ReturnType, $"{method.Name} return");

            foreach (var parameter in method.GetParameters())
            {
                CheckType(parameter.ParameterType, $"{method.Name}({parameter.Name})");
            }
        }

        offenders.Should().BeEmpty(
            because: "ITicketVendorService must expose only Application-layer DTOs, primitives, NodaTime, and BCL collection types in its signatures (design-rules §15 connector pattern); offenders: "
                     + string.Join(", ", offenders));

        void CheckType(Type type, string location)
        {
            foreach (var probed in EnumerateTypes(type))
            {
                var ns = probed.Namespace ?? string.Empty;
                if (ForbiddenNamespacePrefixes.Any(p => ns.StartsWith(p, StringComparison.Ordinal)))
                {
                    offenders.Add($"{location}: {probed.FullName}");
                }
            }
        }

        // Walk generic arguments so we catch forbidden types inside
        // Task<IReadOnlyList<...>>, IEnumerable<...>, etc.
        static IEnumerable<Type> EnumerateTypes(Type type)
        {
            yield return type;
            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    foreach (var inner in EnumerateTypes(arg))
                        yield return inner;
                }
            }
        }
    }

    [HumansFact]
    public void ITicketVendorService_AllDtoTypesLiveBesideThePort()
    {
        // Strict allowlist: every type surfaced by the interface must be a
        // primitive, void/string, System.*, NodaTime.*, or live beside the port —
        // namespace Humans.Tickets.Contracts *and* declared in the port's own assembly.
        // The assembly clause matters since the move: the Humans.Tickets.Contracts leaf
        // shares that namespace, and re-exporting a leaf type here would put Tickets'
        // boundary vocabulary in front of every future vendor adapter. Anything else —
        // Humans.Domain entities, a section's types, vendor SDKs — is a boundary leak.
        var portAssembly = typeof(ITicketVendorService).Assembly;
        var offenders = new List<string>();

        foreach (var method in typeof(ITicketVendorService).GetMethods())
        {
            Inspect(method.ReturnType, $"{method.Name} return");
            foreach (var p in method.GetParameters())
                Inspect(p.ParameterType, $"{method.Name}({p.Name})");
        }

        offenders.Should().BeEmpty(
            because: $"custom types surfaced by ITicketVendorService must live beside the port in {PortNamespace}, in the port's own assembly; offenders: "
                     + string.Join(", ", offenders));

        void Inspect(Type type, string location)
        {
            foreach (var probed in Walk(type))
            {
                var ns = probed.Namespace ?? string.Empty;

                if (probed.IsPrimitive) continue;
                if (probed == typeof(void) || probed == typeof(string)) continue;
                if (ns.StartsWith("System", StringComparison.Ordinal)) continue;
                if (ns.StartsWith("NodaTime", StringComparison.Ordinal)) continue;
                if (string.Equals(ns, PortNamespace, StringComparison.Ordinal)
                    && probed.Assembly == portAssembly) continue;

                offenders.Add($"{location}: {probed.FullName} (namespace {ns})");
            }
        }

        static IEnumerable<Type> Walk(Type type)
        {
            yield return type;
            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                    foreach (var inner in Walk(arg))
                        yield return inner;
            }
        }
    }

    [HumansFact]
    public void BothAdaptersLiveInThisSectionAndAreInternal()
    {
        foreach (var impl in new[] { typeof(TicketTailorService), typeof(StubTicketVendorService) })
        {
            impl.Namespace.Should().Be("Humans.TicketTailor.Services",
                because: "the adapters own HttpClient, JSON parsing and TicketTailor-specific response shapes; they belong to the section that gets deleted when the vendor changes");
            impl.IsPublic.Should().BeFalse(
                because: "nothing outside this project may name an adapter — Section.Register is the only thing that binds one (HUM0034)");
            typeof(ITicketVendorService).IsAssignableFrom(impl).Should().BeTrue();
        }
    }

    [HumansFact]
    public void TheSectionExportsNothingButItsSectionMarker()
    {
        typeof(Section).Assembly.GetExportedTypes()
            .Select(t => t.FullName)
            .Should().BeEquivalentTo(["Humans.TicketTailor.Section"],
                because: "the adapter publishes no contract: consumers of ticketing talk to Humans.Tickets, and this project is one implementation of a Base port");
    }
}
