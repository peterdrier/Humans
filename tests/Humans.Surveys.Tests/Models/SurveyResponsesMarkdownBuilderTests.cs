using AwesomeAssertions;
using AngleSharp.Html.Parser;
using Markdig;
using NodaTime;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Tests.Models;

public sealed class SurveyResponsesMarkdownBuilderTests
{
    private static readonly Instant Submitted = Instant.FromUtc(2026, 6, 4, 9, 30);

    private static SurveyExportQuestion Choice(Guid id, string prompt, params (string Value, string Label)[] opts) =>
        new(id, prompt, SurveyQuestionType.MultiChoice, opts.Select(o => new SurveyExportOption(o.Value, o.Label)).ToList());

    private static SurveyExportQuestion Text(Guid id, string prompt) =>
        new(id, prompt, SurveyQuestionType.ShortText, []);

    private static SurveyExportQuestion Grid(Guid id, string prompt) =>
        new(
            id, prompt, SurveyQuestionType.Grid,
            [new SurveyExportOption("morning", "Morning")],
            GridSelectionMode.Single,
            [new SurveyExportGridRow("monday", "Monday")]);

    [HumansFact]
    public void Build_CommentQuestion_AddsCommentColumn()
    {
        var id = Guid.NewGuid();
        var question = Choice(id, "Agree?", ("no", "No")) with { CommentPrompt = "Why?" };

        var markdown = SurveyResponsesMarkdownBuilder.Build(
            [question],
            [new SurveyExportRow(
                Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted,
                null, null, [new SurveyExportAnswer(id, ["no"], ["No"], "too dear", null)])]);

        markdown.Should().Contain("| Agree? | Agree? (comment) |").And.Contain("| no | too dear |");
    }

    [HumansTheory]
    [Xunit.InlineData(@"one\|pipe")]
    [Xunit.InlineData(@"two\\|pipe")]
    [Xunit.InlineData(@"trailing\")]
    public void Build_LiteralBackslashes_AreKept(string value)
    {
        var textId = Guid.NewGuid();
        var markdown = SurveyResponsesMarkdownBuilder.Build(
            [Text(textId, value)],
            [new SurveyExportRow(
                Guid.NewGuid(), ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, "en", Submitted,
                Guid.NewGuid(), value, [new SurveyExportAnswer(textId, [], [], value, null)])]);
        var html = Markdown.ToHtml(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        using var document = new HtmlParser().ParseDocument(html);

        document.QuerySelectorAll("thead th").Last().TextContent.Should().Be(value);
        var cells = document.QuerySelectorAll("tbody td");
        cells.Should().HaveCount(5);
        cells[3].TextContent.Should().Be(value);
        cells[4].TextContent.Should().Be(value);
    }

    [HumansFact]
    public void Build_MultiChoice_JoinsValuesWithPipe()
    {
        var choiceId = Guid.NewGuid();
        var md = SurveyResponsesMarkdownBuilder.Build(
            [Choice(choiceId, "Pick", ("a", "Apple"), ("b", "Banana"))],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted, null, null,
                    [new SurveyExportAnswer(choiceId, ["a", "b"], ["Apple", "Banana"], null, null)]),
            ]);

        md.Should().Contain("a\\|b");   // pipe inside the joined values is escaped for the MD table
        md.Should().NotContain("Apple");
    }

    [HumansFact]
    public void Build_TextWithPipeOrNewline_IsEscaped()
    {
        var textId = Guid.NewGuid();
        var md = SurveyResponsesMarkdownBuilder.Build(
            [Text(textId, "Comments")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted, null, null,
                    [new SurveyExportAnswer(textId, [], [], "line one\nline | two", null)]),
            ]);

        var dataLine = md.Split('\n')[2];   // header, separator, then the single data row
        dataLine.Should().Contain("line one line \\| two");   // newline → space, pipe → \|
        dataLine.Should().NotContain("\n");
    }

    [HumansFact]
    public void Build_GridSelections_AreStableJson()
    {
        var gridId = Guid.NewGuid();
        var md = SurveyResponsesMarkdownBuilder.Build(
            [Grid(gridId, "Availability")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en", Submitted, null, null,
                    [
                        new SurveyExportAnswer(
                            gridId, [], [], null, null,
                            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                            {
                                ["monday"] = ["morning"],
                            },
                            [new ResolvedGridSelection("monday", "Monday", ["morning"], ["Morning"])]),
                    ]),
            ]);

        md.Should().Contain(@"{""monday"":[""morning""]}");
        md.Should().NotContain("Monday");
        md.Should().NotContain("Morning");
    }

    [HumansFact]
    public void Build_RankedBallot_IsStableJson()
    {
        var questionId = Guid.NewGuid();
        var md = SurveyResponsesMarkdownBuilder.Build(
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

        md.Should().Contain(@"{""RankGroups"":[[""a""]],""Rejected"":[""b""]}");
        md.Should().NotContain("Apple");
        md.Should().NotContain("Banana");
    }

    [HumansFact]
    public void Build_NonIdentifiedRow_LeavesNameBlank()
    {
        var textId = Guid.NewGuid();
        var md = SurveyResponsesMarkdownBuilder.Build(
            [Text(textId, "Comments")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, "en", Submitted,
                    UserId: null, UserName: null,
                    [new SurveyExportAnswer(textId, [], [], "hi", null)]),
            ]);

        var lines = md.Split('\n');
        lines[0].Should().Contain("user_name");
        // Data row: | CompletionTracked | UserSpecificLink | <ts> |  | hi |  — user_name cell is empty.
        var cells = lines[2].Split('|');
        cells[4].Trim().Should().BeEmpty();
    }

    [HumansFact]
    public void Build_IdentifiedRow_FillsUserName()
    {
        var textId = Guid.NewGuid();
        var md = SurveyResponsesMarkdownBuilder.Build(
            [Text(textId, "Comments")],
            [
                new SurveyExportRow(
                    Guid.NewGuid(), ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, "en", Submitted,
                    Guid.NewGuid(), "Sparkle",
                    [new SurveyExportAnswer(textId, [], [], "hi", null)]),
            ]);

        md.Should().Contain("Sparkle");
    }
}
