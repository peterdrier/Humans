using AwesomeAssertions;
using GoogleGroupSyncService = Humans.GoogleIntegration.Services.GoogleGroupSyncService;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// The group sync may not name a Google SDK type, which is what keeps the SDK behind the
/// connector interfaces.
/// </summary>
public class GoogleIntegrationArchitectureTests
{
    // ── GoogleGroupSyncService ──────────────────────────────────────────────

    [HumansFact]
    public void GoogleGroupSyncService_NamesNoGoogleSdkType() =>
        // Was an assertion about the assembly's references, which held while the service lived
        // in Humans.Application. The section owns the Google.Apis.* packages now, so the
        // statement moved down to the type (G5-SECTION-TEMPLATE.md step 11).
        GoogleSdkContainment.AssertNamesNoGoogleSdkType(typeof(GoogleGroupSyncService));
}
