using Humans.Domain.ValueObjects;

namespace Humans.Domain.Entities;

public class SurveyQuestionOption
{
    public Guid Id { get; init; }
    public Guid QuestionId { get; init; }
    public int Order { get; set; }
    public string Value { get; set; } = string.Empty;          // stable machine value, not localised
    public LocalizedText Label { get; set; } = LocalizedText.Empty;
    public SurveyQuestion Question { get; set; } = null!;
}
