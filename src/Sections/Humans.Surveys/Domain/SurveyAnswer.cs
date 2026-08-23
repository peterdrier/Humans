namespace Humans.Surveys.Domain;

internal sealed class SurveyAnswer
{
    public Guid Id { get; init; }
    public Guid ResponseId { get; init; }
    public Guid QuestionId { get; init; }
    public List<string> SelectedOptionValues { get; set; } = [];   // jsonb
    public Dictionary<string, List<string>>? GridSelections { get; set; } // jsonb; row value → column values
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
    public SurveyResponse Response { get; set; } = null!;
}
