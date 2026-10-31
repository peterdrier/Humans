using System.Globalization;
using Xunit;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Models;
using Humans.Surveys.Services;
using NodaTime;

namespace Humans.Surveys.Tests.Models;

public sealed class SurveyResultsBuilderTests
{
    [HumansFact]
    public void Identified_respondents_are_ordered_by_submission_time_then_response_id()
    {
        var time = Instant.FromUtc(2026, 1, 1, 0, 0);
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var results = new SurveyResultsView(Guid.NewGuid(), "Results", SurveyStatus.Closed, 4, 4, 1,
            new SurveyFunnel(4, 4, 0, 0), [], [
                new RespondentDetail(Guid.NewGuid(), "Later", time + Duration.FromHours(1), []) { ResponseId = Guid.NewGuid() },
                new RespondentDetail(Guid.NewGuid(), "Second", time, []) { ResponseId = secondId },
                new RespondentDetail(Guid.NewGuid(), "First", time, []) { ResponseId = firstId },
                new RespondentDetail(Guid.NewGuid(), "Undated", null, []) { ResponseId = Guid.NewGuid() },
            ]);

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 4, SurveyResultsScope.Combined));

        model.Respondents.Select(respondent => respondent.Name).Should().Equal("Undated", "First", "Second", "Later");
    }

    [HumansTheory]
    [InlineData("en", "3.5")]
    [InlineData("es", "3,5")]
    [InlineData("de", "3,5")]
    [InlineData("it", "3,5")]
    [InlineData("fr", "3,5")]
    [InlineData("ca", "3,5")]
    public void Rating_average_uses_ui_language_independently_of_form_parsing(string language, string expected)
    {
        using var culture = new CultureScope(language);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        var results = new SurveyResultsView(
            Guid.NewGuid(), "Results", SurveyStatus.Closed, 2, 2, 1,
            new SurveyFunnel(2, 2, 0, 0),
            [
                new QuestionAggregate(Guid.NewGuid(), "Unanswered", SurveyQuestionType.Rating, [], [], null, []) { PageNumber = 2, Order = 0 },
                new QuestionAggregate(Guid.NewGuid(), "Rating", SurveyQuestionType.Rating, [], [], 3.5, []) { PageNumber = 1, Order = 2 },
                new QuestionAggregate(Guid.NewGuid(), "Earlier", SurveyQuestionType.Rating, [], [], null, []) { PageNumber = 1, Order = 1 },
            ], []);

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 2, SurveyResultsScope.Combined));

        model.Questions.Select(question => question.Prompt).Should().Equal("Earlier", "Rating", "Unanswered");
        model.Questions[1].RatingAverage.Should().Be(expected);
        model.Questions[2].RatingAverage.Should().BeNull();
    }
}
