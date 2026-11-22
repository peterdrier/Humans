using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using NodaTime;

namespace Humans.Web.ModelBinders;

/// <summary>
/// Renders NodaTime <see cref="LocalDate"/> editors as ISO <c>yyyy-MM-dd</c>, the only value
/// <c>&lt;input type="date"&gt;</c> accepts. Without it <c>asp-for</c> writes the culture's long date
/// ("Monday, 19 October 2026") and the browser shows an empty field. MVC only does this for DateTime/DateOnly.
/// </summary>
public sealed class LocalDateEditFormatProvider : IDisplayMetadataProvider
{
    public const string EditFormat = "{0:yyyy-MM-dd}";

    public void CreateDisplayMetadata(DisplayMetadataProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var type = context.Key.ModelType;
        if ((Nullable.GetUnderlyingType(type) ?? type) == typeof(LocalDate))
            context.DisplayMetadata.EditFormatString ??= EditFormat;
    }
}
