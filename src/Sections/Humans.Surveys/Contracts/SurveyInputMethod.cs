namespace Humans.Surveys.Contracts;

/// <summary>Splits the participation funnel: <see cref="UserSpecificLink"/> = tokenised invite; <see cref="Slug"/> = public link.</summary>
public enum SurveyInputMethod
{
    UserSpecificLink = 0,
    Slug = 1
}
