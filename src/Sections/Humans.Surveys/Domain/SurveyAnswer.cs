namespace Humans.Surveys.Domain;

internal sealed class SurveyAnswer
{
    public Guid Id { get; init; }
    public Guid ResponseId { get; init; }
    public Guid QuestionId { get; init; }
    public List<string> SelectedOptionValues { get; set; } = [];
    public Dictionary<string, List<string>>? GridSelections { get; set; } // row value → column values
    public RankedAnswer? RankedValue { get; set; } // ordered rank groups + rejected values
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
    public SurveyResponse Response { get; set; } = null!;
}
