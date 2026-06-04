using AwesomeAssertions;
using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Enums;
using Humans.Web.Models.Survey;
using NodaTime;

namespace Humans.Web.Tests.Models.Survey;

public sealed class SurveyResponsesMarkdownBuilderTests
{
    private static readonly Instant Submitted = Instant.FromUtc(2026, 6, 4, 9, 30);

    private static SurveyExportQuestion Choice(Guid id, string prompt, params (string Value, string Label)[] opts) =>
        new(id, prompt, SurveyQuestionType.MultiChoice, opts.Select(o => new SurveyExportOption(o.Value, o.Label)).ToList());

    private static SurveyExportQuestion Text(Guid id, string prompt) =>
        new(id, prompt, SurveyQuestionType.ShortText, []);

    [HumansFact]
    public void Flattens_multichoice_values_with_pipe_and_uses_values_not_labels()
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
    public void Escapes_pipe_and_collapses_newline_in_freetext()
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
    public void Leaves_user_name_blank_for_non_identified_row()
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
        cells[4].Trim().Should().BeEmpty();   // user_name column
    }

    [HumansFact]
    public void Populates_user_name_for_identified_row()
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
