using AwesomeAssertions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Surveys.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Surveys
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/SurveyArchitectureTests.cs</c>. Its
/// <c>ISurveyService_InheritsISurveyServiceRead</c> test is gone with the interface: the read
/// interface shipped empty and no section ever consumed it, so the assembly boundary plus the
/// one-interface contracts leaf is the whole cross-section story now (design §15 step 5/11).
/// The no-nav and completion-privacy assertions carry over unchanged — they pin domain shape,
/// not layering, and nothing about the move subsumes them.
/// </remarks>
public class SurveysArchitectureTests
{
    [HumansFact]
    public void ContractsExposeOnlyTheCrossSectionSurface()
    {
        // Pins the whole Contracts assembly, so widening Surveys' cross-section surface is a
        // visible diff rather than a silent one. It is one interface: the only consumer outside
        // the section that is not in Shell is SendSurveyReminderJob, which stays in
        // Humans.Infrastructure because recurring jobs have no discovery seam yet (§15 step 6b).
        var contractTypes = typeof(ISurveyReminderSender).Assembly.GetExportedTypes()
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        contractTypes.Should().BeEquivalentTo(["ISurveyReminderSender"]);
    }

    [HumansFact]
    public void ContractsReferenceOnlyTheBottomOfTheGraph()
    {
        typeof(ISurveyReminderSender).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Humans.Application" || a.Name == "Humans.Domain",
                because: "a section's contracts leaf references only the bottom of the graph "
                       + "(memory/architecture/section-project-cycle-fix.md)");
    }

    /// <summary>
    /// Pins the set of types that may inject <see cref="ISurveyRepository"/>: the owning service
    /// and the repository implementation. A new consumer taking the repository directly would
    /// bypass the service layer and the single-writer rule for the <c>survey_*</c> tables.
    /// </summary>
    [HumansFact]
    public void ISurveyRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Surveys.Services.SurveyService",
            "Humans.Surveys.Data.SurveyRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ISurveyRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the survey_* tables must go through SurveyService");
    }

    [HumansFact]
    public void SurveyEntities_HaveNoCrossSectionNavigationProperties()
    {
        typeof(Survey).GetProperty("CreatedByUser").Should().BeNull(
            because: "cross-domain references are bare Guid FKs with no nav (design-rules §6c); resolve via IUserServiceRead");
        typeof(Survey).GetProperty("AudienceTeam").Should().BeNull(
            because: "AudienceTeamId is a bare Guid; resolve the team via ITeamServiceRead");
        typeof(SurveyInvitation).GetProperty("User").Should().BeNull();
        typeof(SurveyResponse).GetProperty("User").Should().BeNull();

        // FKs stay — only navs are absent.
        typeof(Survey).GetProperty("CreatedByUserId").Should().NotBeNull();
        typeof(Survey).GetProperty("AudienceTeamId").Should().NotBeNull();
        typeof(SurveyInvitation).GetProperty("UserId").Should().NotBeNull();
        typeof(SurveyResponse).GetProperty("UserId").Should().NotBeNull();
    }

    [HumansFact]
    public void SurveyInvitation_RecordsCompletionAsBoolWithNoTimestamp()
    {
        typeof(SurveyInvitation).GetProperty("CompletedAt").Should().BeNull(
            because: "a precise completion time would correlate with an anon/completion-tracked response's " +
                     "SubmittedAt and re-identify the invitee (plan Deviation #10)");
        typeof(SurveyInvitation).GetProperty("UpdatedAt").Should().BeNull(
            because: "no UpdatedAt on invitations — it would leak completion timing");

        typeof(SurveyInvitation).GetProperty("Completed")!.PropertyType
            .Should().Be(typeof(bool));
    }

    [HumansFact]
    public void ServiceImplementsIUserDataContributor()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(SurveyService))
            .Should().BeTrue(
                because: "Surveys owns survey_responses and survey_invitations (user-scoped); it must "
                       + "contribute to the GDPR Article 15 export");
    }

    [HumansFact]
    public void ServiceTakesNoCrossSectionRepository()
    {
        var ctor = typeof(SurveyService).GetConstructors().Single();
        ctor.GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                     && !string.Equals(t.Name, "ISurveyRepository", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [HumansFact]
    public void SectionRegistersTheContractAndTheGdprContributorAsTheSameScopedService()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Single(d => d.ServiceType == typeof(ISurveyRepository)).Lifetime
            .Should().Be(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(ISurveyService)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(ISurveyReminderSender)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserDataContributor));
    }

    [HumansFact]
    public void ControllersKeepTheirRoutePrefixes()
    {
        // The public answering wizard, the admin builder and the key-authed analysis API all
        // keep the URLs they had in Shell — a G5 move changes files, never routes.
        RoutePrefixOf("Humans.Surveys.Controllers.SurveyController").Should().Be("Survey");
        RoutePrefixOf("Humans.Surveys.Controllers.SurveyAdminController").Should().Be("Survey/Admin");
        RoutePrefixOf("Humans.Surveys.Controllers.SurveysApiController").Should().Be("api/surveys");
    }

    private static string RoutePrefixOf(string fullName)
    {
        var type = typeof(Section).Assembly.GetType(fullName, throwOnError: true)!;
        return type
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single().Template;
    }
}
