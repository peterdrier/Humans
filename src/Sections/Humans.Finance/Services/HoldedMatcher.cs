using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Humans.Finance.Domain;

namespace Humans.Finance.Services;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct HoldedMatchEntry(
    Guid CategoryId, string AccountId, string Tag);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct HoldedMatchResult(Guid? CategoryId, HoldedMatchSource Source, bool IsManaged = false);

internal static class HoldedMatcher
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

    /// <summary>Trim, collapse whitespace runs to one space, lowercase, fold accents. Two account
    /// names that normalize equal are the same account as far as dedup is concerned.</summary>
    public static string NormalizeAccountName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Account (A) wins over tag (B); else None.</summary>
    public static HoldedMatchResult Match(
        string? bookedAccountId, IReadOnlyList<string> tags, IReadOnlyList<HoldedMatchEntry> map) =>
        Match(bookedAccountId, tags, map, ImmutableHashSet<string>.Empty);

    /// <summary>As above, then a booked account in <paramref name="managedAccountIds"/> (Finance's
    /// registry of accounts outside the budget map) is an Account match with no category — attributed,
    /// so it stays off the Unmatched queue, but outside every budget year's actuals.</summary>
    public static HoldedMatchResult Match(
        string? bookedAccountId, IReadOnlyList<string> tags, IReadOnlyList<HoldedMatchEntry> map,
        IReadOnlySet<string> managedAccountIds)
    {
        if (!string.IsNullOrEmpty(bookedAccountId))
        {
            foreach (var e in map)
                if (string.Equals(e.AccountId, bookedAccountId, StringComparison.Ordinal))
                    return new(e.CategoryId, HoldedMatchSource.Account);
            if (managedAccountIds.Contains(bookedAccountId))
                return new(null, HoldedMatchSource.Account, IsManaged: true);
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
