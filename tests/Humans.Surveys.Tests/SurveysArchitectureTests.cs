using AwesomeAssertions;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
using Microsoft.Extensions.Localization;

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
/// The no-nav assertions were dropped per memory/architecture/no-tests-for-absences.md.
/// The completion-timing one stays: it is a re-identification guard, not a shape claim.
/// </remarks>
public class SurveysArchitectureTests
{
    /// <summary>
    /// A CompletionTracked invitation stays linked to its invitee while the response it
    /// produced is anonymous. Any timestamp on the invitation correlates with that response's
    /// <c>SubmittedAt</c> and re-identifies the respondent, so completion is a bare bool.
    /// </summary>
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
    public void AuditDiscriminatorsAreLiteralsNotDerivedFromTypeNames()
    {
        // These are literal string values we store in the DB. Pinned so a rename can't
        // quietly change them and orphan existing audit_log rows
        // (memory/code/type-name-as-persisted-string.md).
        AuditEntityTypes.Survey.Should().Be("Survey");
        AuditEntityTypes.ReminderJob.Should().Be("SurveyService");
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // This section keeps its own translation file. A type that asks for the shared
        // one gets nothing back and shows people the key name instead of the text.
        // SurveyController shipped exactly that bug: it only showed up when a form
        // failed validation, which no page test goes through.
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
                   + "set renders the key itself and no error");
    }
}
