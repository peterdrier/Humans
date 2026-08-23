namespace Humans.Surveys.Domain;

/// <summary>
/// One publicly served image attached to an Information survey item. Persisted as part of the
/// question's JSONB document because the collection is small (at most five) and aggregate-local.
/// </summary>
internal sealed record SurveyInformationImage(
    Guid Id,
    string StoragePath,
    string ContentType,
    string FileName,
    LocalizedText Label,
    LocalizedText AltText);
