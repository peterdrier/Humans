using AwesomeAssertions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Models;
using NodaTime;

namespace Humans.Surveys.Tests.Models;

public sealed class SurveyResultsBuilderTests
{
    [HumansFact]
    public void Unique_scope_combines_identified_and_completion_tracked_responses()
    {
        var questionId = Guid.NewGuid();
        var results = Results(questionId);
        var export = Export(
            questionId,
            Row(questionId, ResponseAnonymity.Identified, "yes"),
            Row(questionId, ResponseAnonymity.CompletionTracked, "yes"),
            Row(questionId, ResponseAnonymity.Anonymous, "no"));

        var model = SurveyResultsBuilder.Build(results, export, SurveyResultsScope.Unique);

        model.SelectedResponseCount.Should().Be(2);
        model.Questions.Single().OptionCounts.Should().ContainEquivalentOf(
            new OptionCount("yes", "Yes", 2, 100));
        model.Questions.Single().OptionCounts.Should().ContainEquivalentOf(
            new OptionCount("no", "No", 0, 0));
    }

    [HumansFact]
    public void Anonymous_scope_excludes_identified_and_completion_tracked_responses()
    {
        var questionId = Guid.NewGuid();
        var results = Results(questionId);
        var export = Export(
            questionId,
            Row(questionId, ResponseAnonymity.Identified, "yes"),
            Row(questionId, ResponseAnonymity.CompletionTracked, "yes"),
            Row(questionId, ResponseAnonymity.Anonymous, "no"));

        var model = SurveyResultsBuilder.Build(results, export, SurveyResultsScope.Anonymous);

        model.SelectedResponseCount.Should().Be(1);
        model.Questions.Single().OptionCounts.Should().ContainEquivalentOf(
            new OptionCount("yes", "Yes", 0, 0));
        model.Questions.Single().OptionCounts.Should().ContainEquivalentOf(
            new OptionCount("no", "No", 1, 100));
    }

    private static SurveyResultsView Results(Guid questionId) => new(
        Guid.NewGuid(),
        "Survey",
        SurveyStatus.Closed,
        3,
        3,
        1,
        new SurveyFunnel(3, 2, 1, 1),
        [
            new QuestionAggregate(
                questionId,
                "Choice",
                SurveyQuestionType.SingleChoice,
                [
                    new OptionCount("yes", "Yes", 2, 66.666),
                    new OptionCount("no", "No", 1, 33.333),
                ],
                [],
                null,
                []),
        ],
        []);

    private static SurveyResponseExport Export(
        Guid questionId,
        params SurveyExportRow[] rows) => new(
        Guid.NewGuid(),
        "Survey",
        "en",
        [
            new SurveyExportQuestion(
                questionId,
                "Choice",
                SurveyQuestionType.SingleChoice,
                [
                    new SurveyExportOption("yes", "Yes"),
                    new SurveyExportOption("no", "No"),
                ]),
        ],
        rows);

    private static SurveyExportRow Row(
        Guid questionId,
        ResponseAnonymity anonymity,
        string value) => new(
        Guid.NewGuid(),
        anonymity,
        SurveyInputMethod.Slug,
        "en",
        Instant.FromUtc(2026, 8, 25, 6, 0),
        anonymity == ResponseAnonymity.Identified ? Guid.NewGuid() : null,
        anonymity == ResponseAnonymity.Identified ? "Sparkle" : null,
        [new SurveyExportAnswer(questionId, [value], [value], null, null)]);
}
