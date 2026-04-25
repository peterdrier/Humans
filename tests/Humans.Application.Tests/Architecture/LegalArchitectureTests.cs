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
/// Architecture tests for the §15 Legal-document migration (issue #547a).
/// The Legal section has three data-owning document services that share a
/// single <see cref="ILegalDocumentRepository"/> for
/// <c>legal_documents</c> + <c>document_versions</c>. GitHub I/O is
/// delegated to <see cref="IGitHubLegalDocumentConnector"/>, which lives in
/// Infrastructure so the Application-layer services don't take an Octokit
/// dependency.
///
/// <para>
/// ConsentService is explicitly out of scope for #547a and migrates in
/// sub-task #547b — the <c>consent_records</c> table stays with it.
/// </para>
/// </summary>
public class LegalArchitectureTests
{
    // ── Application-layer services ───────────────────────────────────────────

    [HumansFact]
    public void AdminLegalDocumentService_LivesInApplicationLegalNamespace()
    {
        typeof(AdminLegalDocumentService).Namespace
            .Should().Be("Humans.Application.Services.Legal",
                because: "data-owning Legal services live in Humans.Application per design-rules §2b");
    }

    [HumansFact]
    public void LegalDocumentSyncService_LivesInApplicationLegalNamespace()
    {
        typeof(LegalDocumentSyncService).Namespace
            .Should().Be("Humans.Application.Services.Legal",
                because: "data-owning Legal services live in Humans.Application per design-rules §2b");
    }

    [HumansFact]
    public void LegalDocumentService_LivesInApplicationLegalNamespace()
    {
        typeof(LegalDocumentService).Namespace
            .Should().Be("Humans.Application.Services.Legal",
                because: "all Legal-section services are co-located post-#547a so callers find them predictably");
    }

    [HumansFact]
    public void AdminLegalDocumentService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(AdminLegalDocumentService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ILegalDocumentRepository instead");
    }

    [HumansFact]
    public void LegalDocumentSyncService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(LegalDocumentSyncService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ILegalDocumentRepository instead");
    }

    [HumansFact]
    public void LegalDocumentService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(LegalDocumentService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "the statutes fetcher has zero DB access — GitHub I/O goes via IGitHubLegalDocumentConnector");
    }

    [HumansFact]
    public void AdminLegalDocumentService_TakesRepository()
    {
        var ctor = typeof(AdminLegalDocumentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ILegalDocumentRepository));
    }

    [HumansFact]
    public void LegalDocumentSyncService_TakesRepository()
    {
        var ctor = typeof(LegalDocumentSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ILegalDocumentRepository));
    }

    [HumansFact]
    public void LegalDocumentSyncService_TakesConnector()
    {
        var ctor = typeof(LegalDocumentSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IGitHubLegalDocumentConnector),
            because: "external GitHub I/O must go through the connector — no direct Octokit in Application");
    }

    [HumansFact]
    public void LegalDocumentService_TakesConnector()
    {
        var ctor = typeof(LegalDocumentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IGitHubLegalDocumentConnector),
            because: "external GitHub I/O must go through the connector — no direct Octokit in Application");
    }

    [HumansFact]
    public void AdminLegalDocumentService_DoesNotReferenceOctokit()
    {
        var ctor = typeof(AdminLegalDocumentService).GetConstructors().Single();
        var octokitParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Octokit", StringComparison.Ordinal));

        octokitParam.Should().BeNull(
            because: "Octokit is an Infrastructure concern; Application services go through IGitHubLegalDocumentConnector");
    }

    // ── Repository ───────────────────────────────────────────────────────────

    [HumansFact]
    public void ILegalDocumentRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ILegalDocumentRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories");
    }

    [HumansFact]
    public void LegalDocumentRepository_IsSealed()
    {
        var repoType = typeof(LegalDocumentRepository);
        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension (matches Profile/User/Application repos)");
    }

    // ── Connector — lives in Infrastructure, not Application ─────────────────

    [HumansFact]
    public void IGitHubLegalDocumentConnector_InterfaceLivesInApplication()
    {
        typeof(IGitHubLegalDocumentConnector).Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "connector interfaces live in Application so services can take them without an Octokit reference");
    }

    [HumansFact]
    public void GitHubLegalDocumentConnector_ImplementationLivesInInfrastructure()
    {
        var implType = typeof(Humans.Infrastructure.Services.GitHubLegalDocumentConnector);
        implType.Assembly.GetName().Name
            .Should().Be("Humans.Infrastructure",
                because: "connector implementations carry SDK/transport dependencies that belong in Infrastructure");
    }
}
