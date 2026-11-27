using AwesomeAssertions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;

namespace Humans.Surveys.Tests.Services;

public sealed class SurveyAnswerNormalizationTests
{
    private static QuestionInput ChoiceWithComment(params string[] triggers) => new(
        Guid.NewGuid(), 1, 1, SurveyQuestionType.SingleChoice,
        new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Agree?" }), LocalizedText.Empty,
        false, null, null, LocalizedText.Empty, LocalizedText.Empty, null,
        [new OptionInput(null, 0, "yes", LocalizedText.Empty), new OptionInput(null, 1, "no", LocalizedText.Empty)],
        Comment: new SurveyCommentSettings(LocalizedText.Empty, triggers));

    private static SurveyAnswerInput Answer(QuestionInput question, string choice) =>
        new(question.Id!.Value, [choice], "because", null, null, null);

    [HumansTheory]
    [Xunit.InlineData("no", "because")]
    [Xunit.InlineData("yes", null)]
    public void NormalizeAnswer_CommentTrigger_KeepsTextOnlyWhenPicked(string choice, string? expected)
    {
        var question = ChoiceWithComment("no");

        var (answer, refusal) = SurveyAnswerNormalization.NormalizeAnswer(question, Answer(question, choice));

        refusal.Should().BeNull();
        answer.TextValue.Should().Be(expected);
    }

    [HumansFact]
    public void NormalizeAnswer_CommentWithoutTriggers_KeepsTextForAnyChoice()
    {
        var question = ChoiceWithComment();

        var (answer, _) = SurveyAnswerNormalization.NormalizeAnswer(question, Answer(question, "yes"));

        answer.TextValue.Should().Be("because");
    }
}
