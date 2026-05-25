namespace Humans.Web.Cantina;

/// <summary>
/// Shared CSV cell-quoting helper used by the Cantina CSV writers
/// (<see cref="CantinaRosterCsvWriter"/> and
/// <see cref="CantinaDailyMatrixCsvWriter"/>). Centralizes the
/// RFC 4180 conditional quoting + OWASP CSV-injection escape rules so
/// both writers stay consistent. Extracted on the second copy of the
/// same <c>Quote</c> implementation per CLAUDE.md "extract on the third
/// copy" rule — the third would be the dietary nudge follow-up exports
/// and we'd rather not duplicate sanitization for user-controlled text.
/// </summary>
internal static class CsvCellQuoting
{
    /// <summary>
    /// Applies the cantina-CSV quoting rules to a cell value:
    /// <list type="number">
    ///   <item>OWASP CSV-injection escape — cells beginning with
    ///         <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, <c>\t</c>, or
    ///         <c>\r</c> are prepended with a literal apostrophe so the
    ///         spreadsheet renders them as text instead of evaluating
    ///         them as a formula. Source text includes user-controlled
    ///         profile fields (BurnerName, AllergyOtherText,
    ///         IntoleranceOtherText) so this guard must run before RFC
    ///         4180 quoting.</item>
    ///   <item>RFC 4180 conditional quoting — wrap in double quotes and
    ///         double any embedded quotes if the value contains <c>"</c>,
    ///         <c>,</c>, <c>\n</c>, or <c>\r</c>. Otherwise the value is
    ///         emitted verbatim so the export stays readable in
    ///         spreadsheets without parser warnings.</item>
    /// </list>
    /// </summary>
    public static string Quote(string s)
    {
        if (s.Length == 0)
            return string.Empty;

        var escaped = NeedsFormulaEscape(s) ? "'" + s : s;
        if (escaped.IndexOfAny(['"', ',', '\n', '\r']) < 0)
            return escaped;

        return "\"" + escaped.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static bool NeedsFormulaEscape(string s) =>
        !string.IsNullOrEmpty(s) && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
}
