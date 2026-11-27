namespace Humans.Surveys.Domain;

/// <summary>
/// A choice question's optional comment box. It shows when the respondent picks one of
/// <see cref="OptionValues"/>, or any option when that list is empty; the comment is stored as the choice
/// answer's text.
/// </summary>
internal sealed class SurveyCommentSettings : IEquatable<SurveyCommentSettings>
{
    public SurveyCommentSettings(LocalizedText prompt, IReadOnlyList<string> optionValues)
    {
        Prompt = prompt;
        OptionValues = optionValues;
    }

    public LocalizedText Prompt { get; }

    public IReadOnlyList<string> OptionValues { get; }

    /// <summary>True when the comment box shows for <paramref name="selected"/>.</summary>
    public bool IsTriggeredBy(IReadOnlyCollection<string> selected)
        => selected.Count > 0
            && (OptionValues.Count == 0 || selected.Any(value => OptionValues.Contains(value, StringComparer.Ordinal)));

    public bool Equals(SurveyCommentSettings? other)
        => other is not null
            && Prompt.Equals(other.Prompt)
            && OptionValues.SequenceEqual(other.OptionValues, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SurveyCommentSettings);

    public override int GetHashCode()
        => OptionValues.Aggregate(Prompt.GetHashCode(), (hash, value) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(value)));
}
