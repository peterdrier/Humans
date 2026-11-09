using System.Globalization;
using AwesomeAssertions;
using Humans.Web.ModelBinders;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using NodaTime;
using Xunit;

namespace Humans.Web.Tests.Infrastructure;

public class LocalDateEditFormatProviderTests
{
    [HumansTheory]
    [InlineData(typeof(LocalDate))]
    [InlineData(typeof(LocalDate?))]
    public void LocalDate_editor_value_is_iso_in_any_culture(Type modelType)
    {
        var context = new DisplayMetadataProviderContext(
            ModelMetadataIdentity.ForType(modelType), ModelAttributes.GetAttributesForType(modelType));
        new LocalDateEditFormatProvider().CreateDisplayMetadata(context);
        var format = context.DisplayMetadata.EditFormatString!;

        // What DefaultHtmlGenerator.FormatValue does for asp-for inputs.
        var rendered = string.Format(CultureInfo.GetCultureInfo("es-ES"), format, new LocalDate(2026, 10, 19));

        rendered.Should().Be("2026-10-19");
    }
}
