namespace Humans.Base.Extensions;

/// <summary>Length bounds for stored and displayed text.</summary>
public static class StringTruncationExtensions
{
    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxLength"/> UTF-16 units, <paramref name="suffix"/>
    /// included. Never splits a surrogate pair: half of one cannot be stored as UTF-8.
    /// </summary>
    public static string Truncate(this string text, int maxLength, string suffix = "")
    {
        if (text.Length <= maxLength)
            return text;

        var length = maxLength - suffix.Length;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return string.Concat(text.AsSpan(0, length), suffix);
    }
}
