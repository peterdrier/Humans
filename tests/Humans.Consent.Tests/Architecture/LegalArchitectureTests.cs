using AwesomeAssertions;
using LegalDocumentSyncService = Humans.Consent.Services.LegalDocumentSyncService;

namespace Humans.Consent.Tests.Architecture;

/// <summary>
/// Architecture tests for the Legal-document migration.
/// </summary>
public sealed class LegalArchitectureTests
{
    [HumansFact]
    public void LegalDocumentSyncService_does_not_reference_octokit()
    {
        var ctor = typeof(LegalDocumentSyncService).GetConstructors().Single();
        var octokitParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Octokit", StringComparison.Ordinal));

        octokitParam.Should().BeNull(
            because: "Octokit is an Infrastructure concern; Application services go through IGitHubLegalDocumentConnector");
    }
}
