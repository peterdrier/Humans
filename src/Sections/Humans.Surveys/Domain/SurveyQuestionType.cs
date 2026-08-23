namespace Humans.Surveys.Domain;

/// <summary>Question input kinds. Choice types carry options and can drive branching; Grid reuses options as columns but cannot drive branching.</summary>
internal enum SurveyQuestionType
{
    SingleChoice = 0,
    MultiChoice = 1,
    ShortText = 2,
    LongText = 3,
    Rating = 4,
    Grid = 5,
}
