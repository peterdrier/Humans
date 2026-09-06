using Xunit;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// The Google Workspace SDK bridge interfaces gate every <c>Google.Apis.*</c> call made by
/// <c>GoogleWorkspaceSyncService</c>. The tests below are the guarantee that the bridge
/// surface stays shape-neutral and that the section's own service layer names no SDK type.
/// </summary>
public class GoogleWorkspaceSyncBridgeArchitectureTests
{
    /// <summary>
    /// Every bridge interface — enforced below so adding a new one forces the author to
    /// add it to the architecture test suite as well.
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
}
