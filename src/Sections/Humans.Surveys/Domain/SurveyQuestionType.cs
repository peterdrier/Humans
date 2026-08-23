namespace Humans.Surveys.Domain;

/// <summary>
/// Survey item kinds. Information carries no answer; choice types carry options and can drive
/// branching; Grid reuses options as columns but cannot drive branching.
/// </summary>
internal enum SurveyQuestionType
{
    SingleChoice = 0,
    MultiChoice = 1,
    ShortText = 2,
    LongText = 3,
    Rating = 4,
    Grid = 5,
    Information = 6,
}
