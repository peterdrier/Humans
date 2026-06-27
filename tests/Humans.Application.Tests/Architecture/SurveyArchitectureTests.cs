using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Entities;
using Humans.Infrastructure.Repositories.Surveys;
using SurveyService = Humans.Application.Services.Surveys.SurveyService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the Survey section — born §15-compliant (no caching decorator).
/// Pins the cross-section boundary, the single-writer repository ownership, the FK-only
/// (no-nav) cross-domain references, and the privacy invariant that an invitation records
/// completion as a bare boolean with no timestamp (plan Deviation #10).
/// </summary>
public class SurveyArchitectureTests
{
    [HumansFact]
    public void ISurveyService_InheritsISurveyServiceRead()
    {
        typeof(ISurveyServiceRead).IsAssignableFrom(typeof(ISurveyService))
            .Should().BeTrue(
                because: "ISurveyService is the full Survey surface; external sections inject the narrow " +
                         "ISurveyServiceRead. See memory/architecture/section-read-write-split.md.");
    }

    /// <summary>
    /// Pins the set of types that may inject <see cref="ISurveyRepository"/>. Only the owning service
    /// (reads + writes) and the repository implementation. A new consumer that takes the repo directly
    /// would bypass the service layer and the single-writer rule for the <c>survey_*</c> tables.
    /// </summary>
    [HumansFact]
    public void ISurveyRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Application.Services.Surveys.SurveyService",
            "Humans.Infrastructure.Repositories.Surveys.SurveyRepository",
        };

        var assemblies = new[]
        {
            typeof(SurveyService).Assembly,
            typeof(SurveyRepository).Assembly,
        };

        var consumers = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ISurveyRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the survey_* tables must go through SurveyService; if a new type " +
                     "only reads, route it through ISurveyServiceRead instead of injecting the repository.");
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
}
