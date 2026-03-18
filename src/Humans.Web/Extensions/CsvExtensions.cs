using System.Globalization;
using System.Text;

namespace Humans.Web.Extensions;

public static class CsvExtensions
{
    public static void AppendCsvRow(this StringBuilder builder, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AppendJoin(',', values.Select(ToCsvField));
        builder.AppendLine();
    }

    public static string ToCsvField(this object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
