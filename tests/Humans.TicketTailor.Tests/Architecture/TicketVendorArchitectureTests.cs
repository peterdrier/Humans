using AwesomeAssertions;
using Humans.Application.Interfaces.TicketVendor;
using Humans.TicketTailor.Services;

namespace Humans.TicketTailor.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the connector boundary for the Ticket Tailor
/// integration (issue #555 — §15 Part 1). <c>ITicketVendorService</c> is the port and
/// stays in <c>Humans.Application</c> beside <c>IStripeService</c>; the two adapters live
/// in this section. The interface must never leak HTTP-client or vendor-SDK types across
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
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Net.Http",
        "TicketTailor",
        "Humans.Infrastructure",
    ];

    [HumansFact]
    public void ITicketVendorService_LivesInApplicationInterfacesNamespace()
    {
        typeof(ITicketVendorService).Namespace
            .Should().Be("Humans.Application.Interfaces.TicketVendor",
                because: "the vendor-agnostic port lives in the Application layer, in a folder named for the port rather than for the Tickets section, so nothing reads as an unfinished move (design-rules §1, §15)");
    }

    [HumansFact]
    public void ITicketVendorService_IsDeclaredInApplicationAssembly()
    {
        typeof(ITicketVendorService).Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "the port must be compiled into Humans.Application so Application-layer consumers can reference it without an Infrastructure dependency");
    }

    [HumansFact]
    public void HumansApplicationAssembly_HasNoReferenceToInfrastructureOrVendorSdk()
    {
        var applicationAssembly = typeof(ITicketVendorService).Assembly;

        var referenced = applicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced.Should().NotContain(
            name => name.StartsWith("Humans.Infrastructure", StringComparison.Ordinal),
            because: "Humans.Application must not depend on Humans.Infrastructure — the connector pattern inverts this dependency");

        referenced.Should().NotContain(
            name => name.StartsWith("Humans.TicketTailor", StringComparison.Ordinal),
            because: "Humans.Application must not reference the adapter section; the dependency runs the other way, which is what lets the adapter be deleted for the 2027 vendor");
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
    public void ITicketVendorService_AllDtoTypesLiveInApplicationDtos()
    {
        // Strict allowlist: every type surfaced by the interface must be a
        // primitive, void/string, System.*, NodaTime.*, or live beside the port in
        // Humans.Application.Interfaces.TicketVendor. Anything else — Humans.Domain
        // entities, a section's types, vendor SDKs, etc. — is a boundary leak and an
        // offender, regardless of which assembly it lives in.
        var offenders = new List<string>();

        foreach (var method in typeof(ITicketVendorService).GetMethods())
        {
            Inspect(method.ReturnType, $"{method.Name} return");
            foreach (var p in method.GetParameters())
                Inspect(p.ParameterType, $"{method.Name}({p.Name})");
        }

        offenders.Should().BeEmpty(
            because: "custom types surfaced by ITicketVendorService must live beside the port in Humans.Application.Interfaces.TicketVendor; offenders: "
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
                if (string.Equals(ns, "Humans.Application.Interfaces.TicketVendor", StringComparison.Ordinal)) continue;

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
