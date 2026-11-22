namespace Humans.Pleo.Services;

/// <summary>Bounds vendor text without leaving a surrogate half that cannot be persisted as UTF-8.</summary>
internal static class PleoTextBounds
{
    internal static string? Bound(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength) return value;
        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }
}
