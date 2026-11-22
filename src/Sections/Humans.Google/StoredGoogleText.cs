namespace Humans.Google;

/// <summary>Keeps persisted diagnostics within their column limit without splitting a surrogate pair.</summary>
internal static class StoredGoogleText
{
    internal static string Bound(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return text[..length];
    }
}
