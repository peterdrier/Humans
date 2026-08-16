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
    public void BridgeInterface_DoesNotReferenceGoogleSdkTypes(Type bridge) =>
        // Nothing on a bridge interface — parameters, return types, properties, the
        // interfaces it inherits — may name a Google SDK type. That is what lets the rest
        // of the app call Google without ever compiling against the SDK.
        GoogleSdkContainment.AssertNamesNoGoogleSdkType(bridge);

    // ── Assembly cleanliness ─────────────────────────────────────────────────

    [HumansFact]
    public void HumansApplication_HasNoGoogleApisAssemblyReference()
    {
        // Structural guarantee: the Application csproj does not
        // (transitively) reference any Google.Apis.* assembly. Without this,
        // the whole point of the bridge collapses — a service could grab
        // an SDK type anyway.
        // Loaded by name rather than anchored on a typeof: the connectors moved into the
        // section at G5, so a type anchor would otherwise have relocated this sweep wholesale
        // onto Humans.GoogleIntegration - which does reference the SDK - and either failed or,
        // written as a "does not contain", passed while covering nothing
        // (G5-SECTION-TEMPLATE.md step 11). The previous anchor was UserInfo, on the reasoning
        // that the cross-section read model could not leave Base; it left for
        // Humans.Users.Contracts at lane 2 (nobodies-collective/Humans#866) and the guard below
        // caught it. Naming the assembly directly retires the whole anchor-drift failure mode:
        // there is no type left to follow somewhere else.
        var applicationAssembly = Assembly.Load(new AssemblyName("Humans.Application"));

        var referenced = applicationAssembly.GetReferencedAssemblies();

        referenced
            .Should().NotContain(
                a => (a.Name ?? string.Empty).StartsWith("Google.Apis", StringComparison.Ordinal),
                because: "Humans.Application must stay free of Google SDK references; Google API calls live behind bridge interfaces in Humans.Infrastructure");
    }
}
