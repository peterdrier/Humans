namespace Humans.Domain.Entities;

public class SurveyAnswer
{
    public Guid Id { get; init; }
    public Guid ResponseId { get; init; }
    public Guid QuestionId { get; init; }
    public List<string> SelectedOptionValues { get; set; } = [];   // jsonb
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
    public SurveyResponse Response { get; set; } = null!;
}
