using System.Globalization;
using Xunit;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Models;
using Humans.Surveys.Services;

namespace Humans.Surveys.Tests.Models;

public sealed class SurveyResultsBuilderTests
{
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
                new QuestionAggregate(Guid.NewGuid(), "Rating", SurveyQuestionType.Rating, [], [], 3.5, []),
                new QuestionAggregate(Guid.NewGuid(), "Unanswered", SurveyQuestionType.Rating, [], [], null, []),
            ], []);

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 2, SurveyResultsScope.Combined));

        model.Questions[0].RatingAverage.Should().Be(expected);
        model.Questions[1].RatingAverage.Should().BeNull();
    }
}
