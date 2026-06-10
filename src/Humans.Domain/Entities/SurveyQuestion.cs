using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Domain.Entities;

public class SurveyQuestion
{
    public Guid Id { get; init; }
    public Guid SurveyId { get; init; }
    public int PageNumber { get; set; }
    public int Order { get; set; }
    public SurveyQuestionType Type { get; set; }
    public LocalizedText Prompt { get; set; } = LocalizedText.Empty;
    public LocalizedText HelpText { get; set; } = LocalizedText.Empty;
    public bool IsRequired { get; set; }
    public int? RatingMin { get; set; }
    public int? RatingMax { get; set; }
    public LocalizedText RatingMinLabel { get; set; } = LocalizedText.Empty;
    public LocalizedText RatingMaxLabel { get; set; } = LocalizedText.Empty;
    public BranchCondition? ShowIf { get; set; }
    public Survey Survey { get; set; } = null!;
    public ICollection<SurveyQuestionOption> Options { get; set; } = new List<SurveyQuestionOption>();
}
