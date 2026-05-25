using System.Runtime.InteropServices;
using System.Text;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Finance;

[StructLayout(LayoutKind.Auto)]
public readonly record struct HoldedMatchEntry(
    Guid CategoryId, string AccountId, int AccountNum, string Tag);

[StructLayout(LayoutKind.Auto)]
public readonly record struct HoldedMatchResult(Guid? CategoryId, HoldedMatchSource Source);

public static class HoldedMatcher
{
    /// <summary>Lowercase, drop every non-alphanumeric (Holded strips tag separators).</summary>
    public static string NormalizeTag(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Account (A) wins over tag (B); else None.</summary>
    public static HoldedMatchResult Match(
        string? bookedAccountId, IReadOnlyList<string> tags, IReadOnlyList<HoldedMatchEntry> map)
    {
        if (!string.IsNullOrEmpty(bookedAccountId))
        {
            foreach (var e in map)
                if (string.Equals(e.AccountId, bookedAccountId, StringComparison.Ordinal))
                    return new(e.CategoryId, HoldedMatchSource.Account);
        }
        var normTags = tags.Select(NormalizeTag).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (normTags.Count > 0)
        {
            foreach (var e in map)
                if (normTags.Contains(NormalizeTag(e.Tag)))
                    return new(e.CategoryId, HoldedMatchSource.Tag);
        }
        return new(null, HoldedMatchSource.None);
    }
}
