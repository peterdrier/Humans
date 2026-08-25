using System.Text.Json;
using AwesomeAssertions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Models;
using NodaTime;

namespace Humans.Surveys.Tests.Models;

public sealed class SurveyJsonExportBuilderTests
{
    [HumansFact]
    public void Keeps_grid_schema_once_and_response_answers_compact()
    {
        var questionId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(),
            "Availability",
            "en",
            [
                new SurveyExportQuestion(
                    questionId,
                    "When can you help?",
                    SurveyQuestionType.Grid,
                    [
                        new SurveyExportOption("morning", "Morning"),
                        new SurveyExportOption("afternoon", "Afternoon"),
                    ],
                    GridSelectionMode.Multiple,
                    [new SurveyExportGridRow("monday", "Monday")]),
            ],
            [
                new SurveyExportRow(
                    Guid.NewGuid(),
                    ResponseAnonymity.Anonymous,
                    SurveyInputMethod.Slug,
                    "en",
                    Instant.FromUtc(2026, 8, 25, 6, 0),
                    null,
                    null,
                    [
                        new SurveyExportAnswer(
                            questionId,
                            [],
                            [],
                            null,
                            null,
                            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                            {
                                ["monday"] = ["morning", "afternoon"],
                            },
                            [
                                new ResolvedGridSelection(
                                    "monday",
                                    "Monday",
                                    ["morning", "afternoon"],
                                    ["Morning", "Afternoon"]),
                            ]),
                    ]),
            ]);

        using var json = JsonDocument.Parse(SurveyJsonExportBuilder.Build(export));

        var question = json.RootElement.GetProperty("Questions")[0];
        question.GetProperty("GridRows")[0].GetProperty("Label").GetString().Should().Be("Monday");
        question.GetProperty("Options")[0].GetProperty("Label").GetString().Should().Be("Morning");

        var answer = json.RootElement.GetProperty("Rows")[0].GetProperty("Answers")[0];
        answer.GetProperty("GridSelections").GetProperty("monday")[0].GetString().Should().Be("morning");
        answer.TryGetProperty("GridSelectionLabels", out _).Should().BeFalse();
        answer.TryGetProperty("SelectedLabels", out _).Should().BeFalse();
        json.RootElement.GetProperty("Rows")[0].GetProperty("SubmittedAt").GetString()
            .Should().Be("2026-08-25T06:00:00Z");
    }

    [HumansFact]
    public void Omits_null_identity_and_unused_answer_fields()
    {
        var questionId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(),
            "Comments",
            "en",
            [new SurveyExportQuestion(questionId, "Comment", SurveyQuestionType.ShortText, [])],
            [
                new SurveyExportRow(
                    Guid.NewGuid(),
                    ResponseAnonymity.CompletionTracked,
                    SurveyInputMethod.UserSpecificLink,
                    "en",
                    Instant.FromUtc(2026, 8, 25, 6, 0),
                    null,
                    null,
                    [new SurveyExportAnswer(questionId, [], [], "Hello", null)]),
            ]);

        using var json = JsonDocument.Parse(SurveyJsonExportBuilder.Build(export));
        var row = json.RootElement.GetProperty("Rows")[0];
        row.TryGetProperty("UserId", out _).Should().BeFalse();
        row.TryGetProperty("UserName", out _).Should().BeFalse();

        var answer = row.GetProperty("Answers")[0];
        answer.GetProperty("TextValue").GetString().Should().Be("Hello");
        answer.TryGetProperty("SelectedValues", out _).Should().BeFalse();
        answer.TryGetProperty("RatingValue", out _).Should().BeFalse();
        answer.TryGetProperty("GridSelections", out _).Should().BeFalse();
    }
}
