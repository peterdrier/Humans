namespace Humans.Surveys.Domain;

/// <summary>Question input kinds. Choice types (<see cref="SingleChoice"/>/<see cref="MultiChoice"/>) carry options and can drive branching.</summary>
internal enum SurveyQuestionType
{
    SingleChoice = 0,
    MultiChoice = 1,
    ShortText = 2,
    LongText = 3,
    Rating = 4
}
