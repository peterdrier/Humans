using System.Text;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace Humans.Web.Extensions;

/// <summary>
/// Localized display of enum values via the SharedResource key convention
/// <c>Enum_{TypeName}_{Value}</c> (e.g. <c>Enum_CampVibe_LiveMusic</c>).
/// A missing key falls back to the PascalCase name split into words, which the
/// localization sweep still flags — so untranslated values stay visible.
/// </summary>
public static class EnumLocalizationExtensions
{
    /// <summary>Localized display text for a single enum value.</summary>
    public static string EnumDisplay<TEnum>(this IStringLocalizer<SharedResource> localizer, TEnum value)
        where TEnum : struct, Enum
    {
        var localized = localizer[$"Enum_{typeof(TEnum).Name}_{value}"];
        return localized.ResourceNotFound ? Humanize(value.ToString()) : localized.Value;
    }

    /// <summary>
    /// Select-list items for every value of <typeparamref name="TEnum"/> with localized
    /// display text. Item values are the enum names, matching how the model binder and the
    /// previous <c>new SelectList(Enum.GetValues&lt;T&gt;())</c> call sites round-trip.
    /// </summary>
    public static List<SelectListItem> EnumSelectItems<TEnum>(this IStringLocalizer<SharedResource> localizer, TEnum? selected = null)
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(v => new SelectListItem(
            localizer.EnumDisplay(v),
            v.ToString(),
            selected.HasValue && EqualityComparer<TEnum>.Default.Equals(v, selected.Value)))];

    private static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }
}
