using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace Humans.Application.Csv;

/// <summary>
/// The one place CSV conventions are defined. Every CSV read or write in the
/// app builds its <see cref="CsvReader"/>/<see cref="CsvWriter"/> from these
/// factories — hand-rolled CSV splitting, quoting, or escaping is not allowed
/// anywhere else (memory/code/csv-use-csvhelper.md).
/// </summary>
public static class HumansCsv
{
    /// <summary>UTF-8 with BOM — Excel needs the BOM to detect UTF-8 in downloads.</summary>
    public static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Reading defaults for user-supplied files: invariant culture, delimiter
    /// detection (Spanish/European Excel saves semicolon-delimited CSV),
    /// whitespace-and-case-forgiving header matching, trimmed fields.
    /// Call sites may tweak the returned instance (e.g. <c>AllowComments</c>).
    /// </summary>
    public static CsvConfiguration ReadConfig() => new(CultureInfo.InvariantCulture)
    {
        DetectDelimiter = true,
        PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
        TrimOptions = TrimOptions.Trim,
    };

    /// <summary>
    /// Writing defaults for exports: invariant culture, RFC 4180 CRLF (pinned —
    /// <c>Environment.NewLine</c> would differ between Windows dev and the Linux
    /// deploy), and OWASP CSV-injection escaping. The injection character set is
    /// set explicitly because CsvHelper's default omits <c>\t</c> and <c>\r</c>.
    /// </summary>
    public static CsvConfiguration WriteConfig() => new(CultureInfo.InvariantCulture)
    {
        NewLine = "\r\n",
        InjectionOptions = InjectionOptions.Escape,
        InjectionCharacters = ['=', '+', '-', '@', '\t', '\r'],
        InjectionEscapeCharacter = '\'',
        // CsvHelper's default only quotes when the field contains the configured
        // NewLine string ("\r\n"), so a field with a bare \n would corrupt the
        // record structure. Quote on either line-break character individually.
        ShouldQuote = args =>
            !string.IsNullOrEmpty(args.Field)
            && (args.Field.Contains(args.Row.Configuration.Quote)
                || args.Field.Contains(args.Row.Configuration.Delimiter, StringComparison.Ordinal)
                || args.Field.Contains('\r', StringComparison.Ordinal)
                || args.Field.Contains('\n', StringComparison.Ordinal)),
    };

    /// <summary>
    /// Builds a UTF-8 (BOM) CSV byte payload — the standard body for a
    /// <c>text/csv</c> file result. <paramref name="configure"/> may adjust the
    /// write configuration before the writer is created.
    /// </summary>
    public static byte[] WriteBytes(Action<CsvWriter> write, Action<CsvConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(write);

        var config = WriteConfig();
        configure?.Invoke(config);

        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, Utf8WithBom))
        using (var csv = new CsvWriter(sw, config))
        {
            write(csv);
        }

        return ms.ToArray();
    }

    /// <summary>Writes one record from loose values (nulls become empty cells) and ends the row.</summary>
    public static void WriteRow(this CsvWriter csv, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(csv);

        foreach (var value in values)
        {
            csv.WriteField(value);
        }

        csv.NextRecord();
    }
}
