using Humans.GoogleIntegration.Contracts;
using System.Reflection;
using AwesomeAssertions;
using Humans.Monitor.Services;

namespace Humans.Monitor.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Google Integration
/// section's <see cref="DriveActivityMonitorService"/> — migrated under issue
/// #554 (split-off from the umbrella migration). The service now lives in
/// <c>Humans.GoogleIntegration.Services</c> and routes all Google
/// SDK calls through <see cref="IGoogleDriveActivityClient"/>. These tests
/// are the compile-time guarantee that the connector boundary does not leak
/// back into the Application project.
/// </summary>
public class DriveActivityMonitorArchitectureTests
{
    // ── Application assembly cleanliness ─────────────────────────────────────

    [HumansFact]
    public void DriveActivityMonitorService_DoesNotReferenceGoogleSdkTypes()
    {
        // Paranoid double-check: the service's module should have no Google.Apis.*
        // types in its metadata references. Catches cases where a stray `using`
        // survives a mass-edit even if csproj doesn't add the package reference.
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
        // It was in Humans.Base.Interfaces.GoogleIntegration until GoogleIntegration's
        // own G5 move, which turned every other connector abstraction internal to that
        // section. This one could not follow them: DriveActivityMonitorService is here, and a
        // section cannot see another section's internals — so the interface and its
        // DriveActivityEvent projection went onto the contracts leaf instead
        // (nobodies-collective/Humans#866, G5-SECTION-TEMPLATE.md step 5b).
        typeof(IGoogleDriveActivityClient).Namespace
            .Should().Be("Humans.GoogleIntegration.Contracts",
                because: "Monitor consumes this connector across an assembly boundary, so it must be public surface on GoogleIntegration's leaf");
    }

    [HumansFact]
    public void IGoogleDriveActivityClient_HasNoGoogleSdkTypesInSignatures()
    {
        // Every method parameter and return type must come from Humans.Application
        // or the BCL — never Google.Apis.*. Enforces the "shape-neutral" contract.
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
