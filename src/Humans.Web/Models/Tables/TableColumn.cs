using System.Globalization;
using System.Text.Encodings.Web;
using Humans.Web.Extensions;
using Microsoft.AspNetCore.Html;
using NodaTime;

namespace Humans.Web.Models.Tables;

/// <summary>
/// Fluent per-column configuration captured by <see cref="TableModelBuilder{TRow}.Column"/> /
/// <see cref="TableModelBuilder{TRow}.Template"/> lambdas.
/// </summary>
public sealed class TableColumnOptions
{
    internal CellFormat? FormatOverride { get; private set; }
    internal string? SortKeyValue { get; private set; }
    internal bool SortDescFirstValue { get; private set; }
    internal bool ClientSortableValue { get; private set; } = true;
    internal ColumnFilterKind FilterKindValue { get; private set; }
    internal string? FilterParamNameValue { get; private set; }
    internal string? FilterValueValue { get; private set; }
    internal IReadOnlyList<string> FilterOptionsValue { get; private set; } = [];
    internal string? CellCssValue { get; private set; }
    internal string? HeaderCssValue { get; private set; }

    /// <summary>
    /// Server mode: make this column sortable under the given sortBy key.
    /// <paramref name="descendingFirst"/> makes the first click sort descending —
    /// the convention for date/amount/count columns (newest/largest first).
    /// </summary>
    public TableColumnOptions Sort(string serverKey, bool descendingFirst = false)
    {
        SortKeyValue = serverKey;
        SortDescFirstValue = descendingFirst;
        return this;
    }

    /// <summary>Client mode: exclude this column from the site.js sort engine.</summary>
    public TableColumnOptions NoSort() { ClientSortableValue = false; return this; }

    public TableColumnOptions Date() { FormatOverride = CellFormat.Date; return this; }
    public TableColumnOptions DateTime() { FormatOverride = CellFormat.DateTime; return this; }
    public TableColumnOptions Currency() { FormatOverride = CellFormat.Currency; return this; }
    public TableColumnOptions Number() { FormatOverride = CellFormat.Number; return this; }
    public TableColumnOptions EnumBadge() { FormatOverride = CellFormat.EnumBadge; return this; }
    public TableColumnOptions BoolIcon() { FormatOverride = CellFormat.BoolIcon; return this; }

    /// <summary>Contains-match filter. Server mode requires paramName + current value.</summary>
    public TableColumnOptions TextFilter(string? paramName = null, string? value = null)
    {
        FilterKindValue = ColumnFilterKind.Text;
        FilterParamNameValue = paramName;
        FilterValueValue = value;
        return this;
    }

    /// <summary>
    /// Exact-match dropdown filter. Client mode may omit options (auto-populated from distinct
    /// cell values by site.js); server mode requires options + paramName + current value.
    /// </summary>
    public TableColumnOptions SelectFilter(
        IEnumerable<string>? options = null, string? paramName = null, string? value = null)
    {
        FilterKindValue = ColumnFilterKind.Select;
        FilterOptionsValue = options?.ToList() ?? [];
        FilterParamNameValue = paramName;
        FilterValueValue = value;
        return this;
    }

    /// <summary>Right-align (text-end) both header and cells — numeric column convention.</summary>
    public TableColumnOptions End()
    {
        CellCssValue = Append(CellCssValue, "text-end");
        HeaderCssValue = Append(HeaderCssValue, "text-end");
        return this;
    }

    public TableColumnOptions Css(string cssClass) { CellCssValue = Append(CellCssValue, cssClass); return this; }
    public TableColumnOptions HeaderCss(string cssClass) { HeaderCssValue = Append(HeaderCssValue, cssClass); return this; }

    private static string Append(string? current, string addition) =>
        string.IsNullOrEmpty(current) ? addition : $"{current} {addition}";
}

public sealed class TableColumn<TRow>(
    string header,
    CellFormat format,
    Func<TRow, object?>? value,
    Func<TRow, IHtmlContent>? template,
    TableColumnOptions options) : ITableColumn
    where TRow : class
{
    private static readonly IHtmlContent NullContent =
        new HtmlString("""<span class="text-muted">—</span>""");
    private static readonly IHtmlContent CheckContent =
        new HtmlString("""<i class="fa-solid fa-check text-success" aria-label="yes"></i>""");

    public string Header { get; } = header;
    public CellFormat Format { get; } = options.FormatOverride ?? format;
    public string? SortKey => options.SortKeyValue;
    public bool SortDescFirst => options.SortDescFirstValue;
    public bool ClientSortable => options.ClientSortableValue;
    public ColumnFilterKind FilterKind => options.FilterKindValue;
    public string? FilterParamName => options.FilterParamNameValue;
    public string? FilterValue => options.FilterValueValue;
    public IReadOnlyList<string> FilterOptions => options.FilterOptionsValue;
    public string? CellCssClass => options.CellCssValue;
    public string? HeaderCssClass => options.HeaderCssValue;

    public string SortType => Format is CellFormat.Date or CellFormat.DateTime
        or CellFormat.Currency or CellFormat.Number ? "number" : "auto";

    public IHtmlContent Cell(object row)
    {
        var typed = (TRow)row;
        if (Format == CellFormat.Template)
            return template!(typed);

        var raw = value!(typed);
        if (raw is null)
            return NullContent;

        return Format switch
        {
            CellFormat.Date => Encoded(((Instant)raw).ToDate()),
            CellFormat.DateTime => Encoded(((Instant)raw).ToDateTime()),
            CellFormat.Currency => Encoded(ToDecimal(raw).ToString("N2", CultureInfo.CurrentCulture)),
            CellFormat.Number => Encoded(ToDecimal(raw).ToString("#,##0.##", CultureInfo.CurrentCulture)),
            CellFormat.EnumBadge => Badge((Enum)raw),
            CellFormat.BoolIcon => (bool)raw ? CheckContent : NullContent,
            _ => Encoded(raw.ToString() ?? string.Empty),
        };
    }

    public string? SortValue(object row)
    {
        if (value is null || !string.Equals(SortType, "number", StringComparison.Ordinal))
            return null;

        var raw = value((TRow)row);
        if (raw is null)
            return string.Empty;

        return Format switch
        {
            CellFormat.Date or CellFormat.DateTime =>
                ((Instant)raw).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            _ => ToDecimal(raw).ToString(CultureInfo.InvariantCulture),
        };
    }

    private static decimal ToDecimal(object raw) =>
        Convert.ToDecimal(raw, CultureInfo.InvariantCulture);

    private static IHtmlContent Encoded(string text) =>
        new HtmlString(HtmlEncoder.Default.Encode(text));

    private static IHtmlContent Badge(Enum enumValue) =>
        new HtmlString(
            $"""<span class="badge {EnumBadgeMap.For(enumValue)}">{HtmlEncoder.Default.Encode(enumValue.ToString())}</span>""");
}
