namespace Humans.Surveys.Domain;

/// <summary>
/// Stored in the question's jsonb document: the collection is small (at most five) and aggregate-local.
/// </summary>
internal sealed record SurveyInformationImage(
    Guid Id,
    string StoragePath,
    string ContentType,
    string FileName,
    LocalizedText Label,
    LocalizedText AltText);
