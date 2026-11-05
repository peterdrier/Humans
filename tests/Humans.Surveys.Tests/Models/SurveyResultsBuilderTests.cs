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
                new RespondentDetail(Guid.NewGuid(), time + Duration.FromHours(1), []) { ResponseId = Guid.NewGuid() },
                new RespondentDetail(Guid.NewGuid(), time, []) { ResponseId = secondId },
                new RespondentDetail(Guid.NewGuid(), time, []) { ResponseId = firstId },
                new RespondentDetail(Guid.NewGuid(), null, []) { ResponseId = Guid.NewGuid() },
            ]);

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 4, SurveyResultsScope.Combined));

        model.Respondents.Select(respondent => respondent.UserId).Should().Equal(
            results.IdentifiedRespondents[3].UserId, results.IdentifiedRespondents[2].UserId,
            results.IdentifiedRespondents[1].UserId, results.IdentifiedRespondents[0].UserId);
    }

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public void Rating_average_uses_ui_language_independently_of_form_parsing(string language)
    {
        var expected = 3.5.ToString("0.0", CultureInfo.GetCultureInfo(language));
        using var culture = new CultureScope(language);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        var results = new SurveyResultsView(
            Guid.NewGuid(), "Results", SurveyStatus.Closed, 2, 2, 1,
            new SurveyFunnel(2, 2, 0, 0),
            [
                new QuestionAggregate(Guid.NewGuid(), "Unanswered", SurveyQuestionType.Rating, [], [], null, []) { PageNumber = 2, Order = 0 },
                new QuestionAggregate(Guid.NewGuid(), "Rating", SurveyQuestionType.Rating, [], [], 3.5, []) { PageNumber = 1, Order = 2 },
                new QuestionAggregate(Guid.NewGuid(), "Earlier", SurveyQuestionType.SingleChoice, [
                    new OptionCount("maybe", "Maybe", 0, 0) { Order = 3 },
                    new OptionCount("yes", "Yes", 2, 100) { Order = 1 },
                    new OptionCount("no", "No", 0, 0) { Order = 2 },
                ], [], null, []) { PageNumber = 1, Order = 1 },
            ], []);

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 2, SurveyResultsScope.Combined));

        model.Questions.Select(question => question.Prompt).Should().Equal("Earlier", "Rating", "Unanswered");
        model.Questions[0].OptionCounts.Select(option => option.Value).Should().Equal("yes", "no", "maybe");
        model.Questions[1].RatingAverage.Should().Be(expected);
        model.Questions[2].RatingAverage.Should().BeNull();
    }
}
