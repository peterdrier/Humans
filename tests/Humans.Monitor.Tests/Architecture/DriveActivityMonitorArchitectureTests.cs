using Humans.GoogleIntegration.Contracts;
using System.Reflection;
using AwesomeAssertions;
using Humans.Monitor.Services;

namespace Humans.Monitor.Tests.Architecture;

/// <summary>
/// Pins the connector boundary: no Google SDK type reaches
/// <see cref="DriveActivityMonitorService"/> or <see cref="IGoogleDriveActivityClient"/>'s
/// signatures. Every Google call goes through the connector abstraction.
/// </summary>
public class DriveActivityMonitorArchitectureTests
{
    [HumansFact]
    public void DriveActivityMonitorService_DoesNotReferenceGoogleSdkTypes()
    {
        // Signature-level only — base types, interfaces, fields and properties across the
        // Monitor module. A Google.Apis type used inside a method body is invisible here;
        // the absence of the package reference in the csproj is what rules that out.
        var module = typeof(DriveActivityMonitorService).Module;
        var referencedTypes = module.GetTypes()
            .SelectMany(t => new[] { t.BaseType }
                .Concat(t.GetInterfaces())
                .Concat(t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(f => f.FieldType))
                .Concat(t.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(p => p.PropertyType)))
            .Where(t => t is not null)
            .Select(t => t!.Namespace ?? string.Empty);

        referencedTypes
            .Should().NotContain(
                ns => ns.StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "the Application-layer service must not see any Google SDK types — they belong behind IGoogleDriveActivityClient");
    }

    // ── IGoogleDriveActivityClient ───────────────────────────────────────────

    [HumansFact]
    public void IGoogleDriveActivityClient_LivesOnGoogleIntegrationsLeaf()
    {
        // Public on the contracts leaf, not internal like GoogleIntegration's other
        // connectors, because Monitor consumes it across an assembly boundary. The compiler
        // does not catch a move into Humans.GoogleIntegration itself: Monitor references
        // that project too, for the <vc:google-sync-log> tag helper.
        typeof(IGoogleDriveActivityClient).Namespace
            .Should().Be("Humans.GoogleIntegration.Contracts",
                because: "Monitor consumes this connector across an assembly boundary, so it must be public surface on GoogleIntegration's leaf");
    }

    [HumansFact]
    public void IGoogleDriveActivityClient_HasNoGoogleSdkTypesInSignatures()
    {
        // Parameters and return types must be BCL or GoogleIntegration.Contracts types —
        // never Google.Apis.*. Enforces the "shape-neutral" contract.
        var methods = typeof(IGoogleDriveActivityClient).GetMethods();

        foreach (var method in methods)
        {
            var types = new[] { method.ReturnType }
                .Concat(method.GetParameters().Select(p => p.ParameterType))
                .SelectMany(UnwrapGenericArgs);

            foreach (var t in types)
            {
                (t.Namespace ?? string.Empty)
                    .Should().NotStartWith("Google.Apis",
                        because: $"{method.Name} leaks a Google SDK type through its signature; connector contracts must be shape-neutral");
            }
        }

        static IEnumerable<Type> UnwrapGenericArgs(Type t)
        {
            yield return t;
            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    foreach (var inner in UnwrapGenericArgs(arg))
                        yield return inner;
                }
            }
        }
    }
}
