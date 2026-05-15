using AwesomeAssertions;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Legal;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AdminLegalDocumentService = Humans.Application.Services.Legal.AdminLegalDocumentService;
using LegalDocumentService = Humans.Application.Services.Legal.LegalDocumentService;
using LegalDocumentSyncService = Humans.Application.Services.Legal.LegalDocumentSyncService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the Legal-document migration.
/// </summary>
public class LegalArchitectureTests
{
    public static TheoryData<Type> LegalServices => new()
    {
        typeof(AdminLegalDocumentService),
        typeof(LegalDocumentSyncService),
        typeof(LegalDocumentService),
    };

    public static TheoryData<Type, Type> RequiredConstructorEdges => new()
    {
        { typeof(AdminLegalDocumentService), typeof(ILegalDocumentRepository) },
        { typeof(LegalDocumentSyncService), typeof(ILegalDocumentRepository) },
        { typeof(LegalDocumentSyncService), typeof(IGitHubLegalDocumentConnector) },
        { typeof(LegalDocumentService), typeof(IGitHubLegalDocumentConnector) },
    };

    [HumansTheory]
    [MemberData(nameof(LegalServices))]
    public void Legal_services_live_in_application_legal_namespace(Type serviceType)
    {
        serviceType.Namespace
            .Should().Be("Humans.Application.Services.Legal",
                because: "data-owning Legal services live in Humans.Application");
    }

    [HumansTheory]
    [MemberData(nameof(LegalServices))]
    public void Legal_services_have_no_dbcontext_constructor_parameter(Type serviceType)
    {
        var ctor = serviceType.GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "Application services must use repositories or connectors instead of DbContext directly");
    }

    [HumansTheory]
    [MemberData(nameof(RequiredConstructorEdges))]
    public void Legal_services_take_required_repository_or_connector(Type serviceType, Type dependencyType)
    {
        var ctor = serviceType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(dependencyType);
    }

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
    public void Legal_repository_has_expected_application_interface_and_sealed_implementation()
    {
        typeof(LegalDocumentRepository).IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension");
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
}
