using AwesomeAssertions;
using Humans.Tickets.Contracts;

namespace Humans.TicketTailor.Tests.Architecture;

/// <summary>
/// Tickets' <c>ITicketVendorService</c> port exposes no HTTP-client or vendor types, which is what
/// makes the 2027 vendor swap a project delete. Who injects the port is pinned in Humans.Web.Tests.
/// </summary>
public class TicketVendorArchitectureTests
{
    private const string PortNamespace = "Humans.Tickets.Contracts";

    // Prefix-matched; add a vendor SDK's root namespace here if one ever shows up.
    private static readonly string[] ForbiddenNamespacePrefixes = ["System.Net.Http"];

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
            because: "ITicketVendorService must expose only the port's DTOs, primitives, NodaTime, and BCL collection types in its signatures; offenders: "
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
        // Strict allowlist: primitives, System.*, NodaTime.*, or types beside the port — namespace
        // Humans.Tickets.Contracts *and* the port's own assembly, since the Contracts leaf shares the
        // namespace and re-exporting it would put Tickets' vocabulary in front of every vendor adapter.
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
}
