using System.Reflection;
using AwesomeAssertions;
using Humans.Application;
using Xunit;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// Architecture tests for the §15 Part 2a Google Workspace SDK bridge
/// interfaces (issue #574). These bridges gate every
/// <c>Google.Apis.*</c> call made by <c>GoogleWorkspaceSyncService</c>, which
/// moves into the Application layer in Part 2b (#575). The tests below are
/// the compile-time guarantee that the bridge surface stays shape-neutral
/// and that the Application assembly does not drift back into a Google SDK
/// dependency.
/// </summary>
public class GoogleWorkspaceSyncBridgeArchitectureTests
{
    /// <summary>
    /// Every bridge interface introduced by Part 2a — enforced below so
    /// adding a new one forces the author to add it to the architecture
    /// test suite as well.
    /// </summary>
    public static readonly IReadOnlyList<Type> BridgeInterfaces =
    [
        typeof(IGoogleGroupMembershipClient),
        typeof(IGoogleGroupProvisioningClient),
        typeof(IGoogleDrivePermissionsClient),
        typeof(IGoogleDirectoryClient)
    ];

    public static IEnumerable<object[]> BridgeInterfaceCases =>
        BridgeInterfaces.Select(t => new object[] { t });

    // ── Namespace + location ─────────────────────────────────────────────────

    [HumansTheory]
    [MemberData(nameof(BridgeInterfaceCases))]
    public void BridgeInterface_LivesInTheConnectorNamespace(Type bridge)
    {
        bridge.Namespace
            .Should().Be(GoogleSdkContainment.ConnectorNamespace,
                because: "the connector interfaces and their SDK-touching implementations sit together in one namespace, which is the section's Google-SDK boundary since the G5 move");
    }

    /// <summary>
    /// The restated form of the old "the bridge lives in Humans.Application" assertion. The
    /// interface and its implementation share an assembly now, so what the bridge buys is
    /// asserted where it is still true: no type in the section's service namespace names an
    /// SDK type.
    /// </summary>
    [HumansFact]
    public void SectionServiceLayer_NamesNoGoogleSdkType() =>
        GoogleSdkContainment.AssertServiceLayerNamesNoGoogleSdkType();

    // ── Shape-neutral surface ────────────────────────────────────────────────

    [HumansTheory]
    [MemberData(nameof(BridgeInterfaceCases))]
    public void BridgeInterface_HasNoGoogleSdkTypesInSignatures(Type bridge)
    {
        // Every method parameter, return type, and the types nested inside
        // generic arguments must live in Humans.* or the BCL — never
        // Google.Apis.*. This is what "shape-neutral" means: the Application
        // layer compiles against the bridge without a Google.Apis.*
        // transitive reference.
        var methods = bridge.GetMethods();

        foreach (var method in methods)
        {
            var types = new[] { method.ReturnType }
                .Concat(method.GetParameters().Select(p => p.ParameterType))
                .SelectMany(UnwrapGenericArgs);

            foreach (var t in types)
            {
                (t.Namespace ?? string.Empty)
                    .Should().NotStartWith("Google.Apis",
                        because: $"{bridge.Name}.{method.Name} leaks a Google SDK type through its signature; connector contracts must be shape-neutral");
            }
        }
    }

    // ── Assembly cleanliness ─────────────────────────────────────────────────

    [HumansFact]
    public void HumansApplication_HasNoGoogleApisAssemblyReference()
    {
        // Structural guarantee: the Application csproj does not
        // (transitively) reference any Google.Apis.* assembly. Without this,
        // the whole point of the bridge collapses — a service could grab
        // an SDK type anyway.
        // Anchored on UserInfo, not on a connector interface: the connectors moved into the
        // section at G5, so this sweep would otherwise have relocated wholesale onto
        // Humans.GoogleIntegration - which does reference the SDK - and either failed or,
        // written as a "does not contain", passed while covering nothing
        // (G5-SECTION-TEMPLATE.md step 11). UserInfo is the cross-section read model every
        // section binds, so it cannot leave Base; the guard below says so if it ever does.
        var applicationAssembly = typeof(UserInfo).Assembly;
        applicationAssembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "this sweep is only meaningful while its anchor type still lives in Humans.Application");

        var referenced = applicationAssembly.GetReferencedAssemblies();

        referenced
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Humans.Application must stay free of Google SDK references; Google API calls live behind bridge interfaces in Humans.Infrastructure");
    }

    [HumansTheory]
    [MemberData(nameof(BridgeInterfaceCases))]
    public void BridgeInterface_DoesNotReferenceGoogleSdkTypes(Type bridge) =>
        // Scoped to the bridge type. It used to walk the whole module, which was a true
        // statement while the interfaces lived in Humans.Application and would now be a walk
        // over the connectors' own assembly.
        GoogleSdkContainment.AssertNamesNoGoogleSdkType(bridge);

    private static IEnumerable<Type> UnwrapGenericArgs(Type t)
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
