namespace Humans.Surveys.Domain;

/// <summary>A Grid row definition: stable machine value plus localized display label.</summary>
internal sealed record SurveyGridRow(string Value, LocalizedText Label);
