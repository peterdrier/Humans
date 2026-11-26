using System.Globalization;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Humans.Testing;

public static class LocalizerSubstitutes
{
    /// <summary>
    /// Makes a substitute's <c>localizer[key, args]</c> format the text its <c>localizer[key]</c> returns,
    /// the way the real localizer does, so a test stubs each key once.
    /// </summary>
    public static T AddFormatting<T>(this T localizer) where T : class, IStringLocalizer
    {
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call =>
        {
            var key = call.ArgAt<string>(0);
            var text = localizer[key];
            var format = text?.Value ?? key;
            return new LocalizedString(key, string.Format(CultureInfo.CurrentCulture, format, call.ArgAt<object[]>(1)));
        });
        return localizer;
    }
}
