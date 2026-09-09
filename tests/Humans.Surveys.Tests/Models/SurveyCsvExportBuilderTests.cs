using Humans.Surveys.Domain;
using System.Text;
using AwesomeAssertions;
using Humans.Surveys.Services;
using Humans.Surveys.Models;
using NodaTime;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Tests.Models;

public sealed class SurveyCsvExportBuilderTests
{
    private static readonly Instant Submitted = Instant.FromUtc(2026, 6, 4, 9, 30);

    private static SurveyExportQuestion Choice(Guid id, string prompt, params (string Value, string Label)[] opts) =>
        new(id, prompt, SurveyQuestionType.MultiChoice,
            opts.Select(o => new SurveyExportOption(o.Value, o.Label)).ToList());

    private static SurveyExportQuestion Text(Guid id, string prompt) =>
        new(id, prompt, SurveyQuestionType.ShortText, []);

    private static SurveyExportQuestion Grid(Guid id, string prompt) =>
        new(
            id, prompt, SurveyQuestionType.Grid,
            [new SurveyExportOption("morning", "Morning"), new SurveyExportOption("afternoon", "Afternoon")],
            GridSelectionMode.Multiple,
            [new SurveyExportGridRow("monday", "Monday")]);

    private static IReadOnlyList<string> Lines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .TrimStart('﻿') // exports carry a UTF-8 BOM so Excel detects the encoding
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n')
            .Split('\n');

    [HumansFact]
    public void Escapes_freetext_with_comma_quote_and_newline_per_rfc4180()
    {
        var textId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(), "T", "en",
            [Text(textId, "Comments")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted, null, null,
                    [new SurveyExportAnswer(textId, [], [], "a, \"b\"\nc", null)]),
            ]);

        var csv = Encoding.UTF8.GetString(SurveyCsvExportBuilder.Build(export));

        // Field is wrapped in quotes; the embedded quotes are doubled; the comma/newline survive inside the quoted field.
        csv.Should().Contain("\"a, \"\"b\"\"\nc\"");
    }

    [HumansFact]
    public void Flattens_multichoice_values_with_pipe_and_uses_values_not_labels()
    {
        var choiceId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(), "T", "en",
            [Choice(choiceId, "Pick", ("a", "Apple"), ("b", "Banana"))],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted, null, null,
                    [new SurveyExportAnswer(choiceId, ["a", "b"], ["Apple", "Banana"], null, null)]),
            ]);

        var lines = Lines(SurveyCsvExportBuilder.Build(export));

        // Last column on the single data row is the choice cell — stable values joined by '|', not labels.
        lines[1].Should().EndWith("a|b");
        lines[1].Should().NotContain("Apple");
    }

    [HumansFact]
    public void Serializes_grid_selections_as_stable_row_to_column_json()
    {
        var gridId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(), "T", "en",
            [Grid(gridId, "Availability")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted, null, null,
                    [
                        new SurveyExportAnswer(
                            gridId, [], [], null, null,
                            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                            {
                                ["monday"] = ["morning", "afternoon"],
                            },
                            [
                                new ResolvedGridSelection(
                                    "monday", "Monday",
                                    ["morning", "afternoon"],
                                    ["Morning", "Afternoon"]),
                            ]),
                    ]),
            ]);

        var csv = Encoding.UTF8.GetString(SurveyCsvExportBuilder.Build(export));

        csv.Should().Contain(@"{""""monday"""":[""""morning"""",""""afternoon""""]}");
        csv.Should().NotContain("Monday");
        csv.Should().NotContain("Morning");
    }

    [HumansFact]
    public void Serializes_ranked_ballot_as_stable_value_json()
    {
        var questionId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(), "T", "en",
            [
                new SurveyExportQuestion(
                    questionId,
                    "Rank dates",
                    SurveyQuestionType.RankedChoice,
                    [new SurveyExportOption("a", "Apple"), new SurveyExportOption("b", "Banana")]),
            ],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                    "en", Submitted, Guid.NewGuid(), "Sparkle",
                    [
                        new SurveyExportAnswer(
                            questionId, [], [], null, null,
                            RankedBallot: new SurveyRankedBallot([["a"]], ["b"])),
                    ]),
            ]);

        var csv = Encoding.UTF8.GetString(SurveyCsvExportBuilder.Build(export));

        csv.Should().Contain("RankGroups");
        csv.Should().Contain("Rejected");
        csv.Should().Contain(@"""""a""""");
        csv.Should().Contain(@"""""b""""");
        csv.Should().NotContain("Apple");
        csv.Should().NotContain("Banana");
    }

    [HumansFact]
    public void Leaves_identity_columns_blank_for_non_identified_row()
    {
        var textId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(), "T", "en",
            [Text(textId, "Comments")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, "en", Submitted,
                    UserId: null, UserName: null,
                    [new SurveyExportAnswer(textId, [], [], "hi", null)]),
            ]);

        var lines = Lines(SurveyCsvExportBuilder.Build(export));

        // Header: response_id,anonymity,input_method,submitted_at,user_id,user_name,Comments
        lines[0].Should().StartWith("response_id,anonymity,input_method,submitted_at,user_id,user_name");

        // user_id (col 5) and user_name (col 6) are empty for a CompletionTracked row.
        var cells = lines[1].Split(',');
        cells[4].Should().BeEmpty();   // user_id
        cells[5].Should().BeEmpty();   // user_name
    }

    [HumansFact]
    public void Populates_identity_columns_for_identified_row()
    {
        var textId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(), "T", "en",
            [Text(textId, "Comments")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, "en", Submitted,
                    userId, "Sparkle",
                    [new SurveyExportAnswer(textId, [], [], "hi", null)]),
            ]);

        var lines = Lines(SurveyCsvExportBuilder.Build(export));

        lines[1].Should().Contain(userId.ToString());
        lines[1].Should().Contain("Sparkle");
    }

    [HumansFact]
    public void Escapes_formula_prefixed_freetext_per_owasp()
    {
        var textId = Guid.NewGuid();
        var export = new SurveyResponseExport(
            Guid.NewGuid(), "T", "en",
            [Text(textId, "Comments")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted, null, null,
                    [new SurveyExportAnswer(textId, [], [], "=HYPERLINK(\"http://evil\")", null)]),
            ]);

        var lines = Lines(SurveyCsvExportBuilder.Build(export));

        // The free-text cell must not reach a spreadsheet starting with '=' —
        // the shared write config prepends an apostrophe.
        var lastCell = lines[1][(lines[1].LastIndexOf(',') + 1)..];
        lastCell.Should().NotStartWith("=");
        lastCell.Should().Contain("'=");
    }
}
