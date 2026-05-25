using AwesomeAssertions;
using Humans.Application.Interfaces.Legal;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using AdminLegalDocumentService = Humans.Application.Services.Legal.AdminLegalDocumentService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the Legal-document migration.
/// </summary>
public class LegalArchitectureTests
{
    [HumansFact]
    public void AdminLegalDocumentService_does_not_reference_octokit()
    {
        var ctor = typeof(AdminLegalDocumentService).GetConstructors().Single();
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
            .Should().Be("Humans.Application",
                because: "connector interfaces live in Application");
        typeof(Humans.Infrastructure.Services.GitHubLegalDocumentConnector).Assembly.GetName().Name
            .Should().Be("Humans.Infrastructure",
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
