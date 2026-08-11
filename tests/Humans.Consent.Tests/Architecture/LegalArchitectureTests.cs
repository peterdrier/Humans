using AwesomeAssertions;
using Humans.Consent.Services;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Consent.Contracts;
using Humans.Consent.Controllers;
using Microsoft.AspNetCore.Mvc;
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

    [HumansFact]
    public void GitHub_legal_document_connector_interface_and_implementation_live_in_correct_layers()
    {
        typeof(IGitHubLegalDocumentConnector).Assembly.GetName().Name
            .Should().Be("Humans.Consent",
                because: "the GitHub connector is shaped by this section's file-naming convention "
                       + "and has no consumer outside it, so it moved in whole at G5");
        typeof(GitHubLegalDocumentConnector).Assembly.GetName().Name
            .Should().Be("Humans.Consent",
                because: "connector implementations carry SDK/transport dependencies");
    }

    [HumansFact]
    public void AdminLegalDocumentsController_LivesUnderLegalAdminRoute()
    {
        RouteFor<AdminLegalDocumentsController>().Should().Be("Legal/Admin",
            because: "admin legal-document pages live at /Legal/Admin/* per memory/architecture/no-admin-url-section.md");
    }

    private static string RouteFor<TController>()
    {
        var route = typeof(TController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single();

        return route.Template;
    }
}
