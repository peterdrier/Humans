using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the connector boundary for the Ticket Tailor
/// integration (issue #555 — §15 Part 1). <c>ITicketVendorService</c> is the
/// Application-layer port; <c>TicketTailorService</c> and
/// <c>StubTicketVendorService</c> are Infrastructure adapters. The interface
/// must never leak HTTP-client or vendor-SDK types across the boundary — its
/// entire signature set (parameters, return types) must be expressible in
/// Application-layer terms (Application DTOs, primitives, NodaTime, BCL
/// collections).
///
/// <para>
/// These tests fail loudly if a future change drags the interface back into
/// <c>Humans.Infrastructure</c>, adds a parameter/return type from an HTTP or
/// vendor-SDK namespace, or accidentally references a type from
/// <c>Humans.Infrastructure</c> inside the Application project.
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

    [Fact]
    public void ITicketVendorService_LivesInApplicationInterfacesNamespace()
    {
        typeof(ITicketVendorService).Namespace
            .Should().Be("Humans.Application.Interfaces",
                because: "the vendor-agnostic port lives in the Application layer; HTTP/SDK adapters are Infrastructure (design-rules §1, §15)");
    }

    [Fact]
    public void ITicketVendorService_IsDeclaredInApplicationAssembly()
    {
        typeof(ITicketVendorService).Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "the port must be compiled into Humans.Application so Application-layer consumers can reference it without an Infrastructure dependency");
    }

    [Fact]
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
            name => name.StartsWith("TicketTailor", StringComparison.Ordinal),
            because: "Humans.Application must not reference the TicketTailor SDK; HTTP I/O is Infrastructure's concern");
    }

    [Fact]
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

    [Fact]
    public void ITicketVendorService_AllDtoTypesLiveInApplicationDtos()
    {
        // Every non-BCL, non-NodaTime, non-primitive type surfaced by the
        // interface must live in Humans.Application.DTOs (the
        // Application-layer vendor-agnostic shape).
        var offenders = new List<string>();

        foreach (var method in typeof(ITicketVendorService).GetMethods())
        {
            Inspect(method.ReturnType, $"{method.Name} return");
            foreach (var p in method.GetParameters())
                Inspect(p.ParameterType, $"{method.Name}({p.Name})");
        }

        offenders.Should().BeEmpty(
            because: "custom types surfaced by ITicketVendorService must live in Humans.Application.DTOs (Application-layer vendor-agnostic DTOs); offenders: "
                     + string.Join(", ", offenders));

        void Inspect(Type type, string location)
        {
            foreach (var probed in Walk(type))
            {
                var ns = probed.Namespace ?? string.Empty;

                // BCL, System.*, NodaTime, primitives — fine.
                if (probed.IsPrimitive) continue;
                if (probed == typeof(void) || probed == typeof(string)) continue;
                if (ns.StartsWith("System", StringComparison.Ordinal)) continue;
                if (ns.StartsWith("NodaTime", StringComparison.Ordinal)) continue;

                // Types declared by this assembly must live in the DTOs namespace.
                if (probed.Assembly == typeof(ITicketVendorService).Assembly &&
                    !string.Equals(ns, "Humans.Application.DTOs", StringComparison.Ordinal))
                {
                    offenders.Add($"{location}: {probed.FullName} (namespace {ns})");
                }
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

    [Fact]
    public void TicketTailorService_LivesInHumansInfrastructureServicesNamespace()
    {
        var infra = Assembly.Load("Humans.Infrastructure");
        var impl = infra.GetExportedTypes()
            .Single(t => string.Equals(t.Name, "TicketTailorService", StringComparison.Ordinal)
                         && typeof(ITicketVendorService).IsAssignableFrom(t));

        impl.Namespace.Should().Be("Humans.Infrastructure.Services",
            because: "the HTTP-backed adapter must stay in Infrastructure — it owns HttpClient, JSON parsing, and TicketTailor-specific response shapes (design-rules §1, §15 connector)");
    }

    [Fact]
    public void StubTicketVendorService_LivesInHumansInfrastructureServicesNamespace()
    {
        var infra = Assembly.Load("Humans.Infrastructure");
        var impl = infra.GetExportedTypes()
            .Single(t => string.Equals(t.Name, "StubTicketVendorService", StringComparison.Ordinal)
                         && typeof(ITicketVendorService).IsAssignableFrom(t));

        impl.Namespace.Should().Be("Humans.Infrastructure.Services",
            because: "the dev-time stub adapter is an Infrastructure concern (DI registration swaps prod HTTP impl for it); it is not an Application-layer interface variant");
    }
}
