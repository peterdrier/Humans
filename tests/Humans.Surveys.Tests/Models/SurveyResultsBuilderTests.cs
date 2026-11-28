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
    public void Build_IdentifiedRespondents_OrderBySubmissionTime()
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

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 4, SurveyResultsScope.Combined), FixedOrgTimeZone.Madrid.Zone);

        model.Respondents.Select(respondent => respondent.UserId).Should().Equal(
            results.IdentifiedRespondents[3].UserId, results.IdentifiedRespondents[2].UserId,
            results.IdentifiedRespondents[1].UserId, results.IdentifiedRespondents[0].UserId);
    }

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public void Build_RatingAverage_UsesUiLanguage(string language)
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

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 2, SurveyResultsScope.Combined), FixedOrgTimeZone.Madrid.Zone);

        model.Questions.Select(question => question.Prompt).Should().Equal("Earlier", "Rating", "Unanswered");
        model.Questions[0].OptionCounts.Select(option => option.Value).Should().Equal("yes", "no", "maybe");
        model.Questions[1].RatingAverage.Should().Be(expected);
        model.Questions[2].RatingAverage.Should().BeNull();
    }

    [HumansFact]
    public void Build_FollowUp_NestsUnderParentWithSurveyNumber()
    {
        var parentId = Guid.NewGuid();
        var followUpId = Guid.NewGuid();
        var results = new SurveyResultsView(Guid.NewGuid(), "Results", SurveyStatus.Closed, 1, 1, 1,
            new SurveyFunnel(1, 1, 0, 0),
            [
                new QuestionAggregate(followUpId, "Why?", SurveyQuestionType.ShortText, [], [], null, []) { PageNumber = 1, Order = 2, FollowUpOf = parentId },
                new QuestionAggregate(parentId, "Price", SurveyQuestionType.SingleChoice, [], [], null, []) { PageNumber = 1, Order = 1 },
                new QuestionAggregate(Guid.NewGuid(), "Else", SurveyQuestionType.LongText, [], [], null, []) { PageNumber = 1, Order = 3 },
            ], []);

        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 1, SurveyResultsScope.Combined), FixedOrgTimeZone.Madrid.Zone);

        model.Questions.Select(q => (q.Prompt, q.Number)).Should().Equal(("Price", 1), ("Else", 3));
        model.Questions[0].FollowUps.Should().ContainSingle().Which.Number.Should().Be(2);
        model.QuestionsById.Keys.Should().Contain(followUpId);
    }
    [HumansFact]
    public void NestedUnder_FollowUpOfFollowUp_KeepsOwnRow()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var results = new SurveyResultsView(Guid.NewGuid(), "Results", SurveyStatus.Closed, 1, 1, 1,
            new SurveyFunnel(1, 1, 0, 0),
            [
                new QuestionAggregate(a, "A", SurveyQuestionType.SingleChoice, [], [], null, []) { PageNumber = 1, Order = 1 },
                new QuestionAggregate(b, "B", SurveyQuestionType.SingleChoice, [], [], null, []) { PageNumber = 1, Order = 2, FollowUpOf = a },
                new QuestionAggregate(c, "C", SurveyQuestionType.ShortText, [], [], null, []) { PageNumber = 1, Order = 3, FollowUpOf = b },
            ], []);
        var model = SurveyResultsBuilder.Build(new SurveyScopedResults(results, 1, SurveyResultsScope.Combined), FixedOrgTimeZone.Madrid.Zone);
        RespondentAnswer Answer(Guid id) => new(id, "", ["x"], null, null);
        var answers = new SurveyRespondentAnswersViewModel([Answer(a), Answer(b), Answer(c)], model.QuestionsById);

        answers.NestedUnder(answers.Answers[1]).Should().Be(a);
        answers.NestedUnder(answers.Answers[2]).Should().BeNull();
    }
}
