namespace Humans.Domain.Enums;

/// <summary>Survey lifecycle. Authored in <see cref="Draft"/>, accepts responses while <see cref="Open"/>, no longer accepts responses once <see cref="Closed"/>.</summary>
public enum SurveyStatus
{
    Draft = 0,
    Open = 1,
    Closed = 2
}
