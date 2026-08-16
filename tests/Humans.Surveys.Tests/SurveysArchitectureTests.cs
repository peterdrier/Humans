using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;

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
    public void AuditDiscriminatorsAreLiteralsNotDerivedFromTypeNames()
    {
        // The audit_log rows already in the database carry these strings and are matched by
        // exact equality on read. Pinning them is what makes a future rename over this section
        // schema-inert (memory/code/type-name-as-persisted-string.md).
        AuditEntityTypes.Survey.Should().Be("Survey");
        AuditEntityTypes.ReminderJob.Should().Be("SurveyService");
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
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // The carve moved every Survey_* key out of SharedResource, so a type still injecting
        // IStringLocalizer<SharedResource> resolves nothing and renders the raw key — a 200 with
        // degraded copy, in every language. SurveyController shipped exactly that on three paths
        // (Survey_QuestionRequired, Survey_ThankYouFallback), and the render tests missed it
        // because both only fire on validation failure and on a survey with no custom thank-you.
        // The views were never at risk: _ViewImports binds the section's localizer for all of them.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && p.ParameterType.GetGenericArguments()[0] != typeof(SurveysResource))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "every Survey_* key lives in SurveysResource; resolving one through another "
                   + "set renders the key itself and no error (§15 step 3b)");
    }


}
