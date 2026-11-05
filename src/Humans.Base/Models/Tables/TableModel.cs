using System.Text.Encodings.Web;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Html;

namespace Humans.Base.Models.Tables;

/// <summary>Entry point: <c>TableModel.For(rows).Column(...).Build()</c> in a view code block.</summary>
public static class TableModel
{
    public static TableModelBuilder<TRow> For<TRow>(IReadOnlyList<TRow> rows) where TRow : class => new(rows);
}

public sealed class TableModelBuilder<TRow> where TRow : class
{
    private readonly IReadOnlyList<TRow> _rows;
    private readonly List<TableColumn<TRow>> _columns = [];
    private readonly Dictionary<string, string?> _hiddenFields = [];
    private TableMode _mode = TableMode.Client;
    private string _emptyMessage = "No results";
    private string? _id;
    private string? _extraCssClass;
    private bool _hasSearchBox;
    private string _searchParamName = "search";
    private string? _searchValue;
    private string _searchPlaceholder = "Search...";
    private string? _sortBy;
    private bool _sortDesc;
    private PagerViewModel? _pager;
    private IReadOnlyList<TableFooterCell> _footerCells = [];
    private Func<object?, IHtmlContent>? _extraFilterContent;
    private Func<TRow, string?>? _rowHref;
    private Func<TRow, string?>? _rowCss;
    private readonly List<(string Name, Func<TRow, string?> Value)> _rowData = [];

    internal TableModelBuilder(IReadOnlyList<TRow> rows) => _rows = rows;

    public TableModelBuilder<TRow> Column(
        string header, Func<TRow, object?> value, Action<TableColumnOptions>? configure = null)
    {
        var options = new TableColumnOptions();
        configure?.Invoke(options);
        _columns.Add(new TableColumn<TRow>(header, CellFormat.Text, value, template: null, options));
        return this;
    }

    /// <summary>
    /// A link cell: <paramref name="text"/> is formatted and sorted like a <see cref="Column"/> value,
    /// and links to <paramref name="href"/> when that returns a URL.
    /// </summary>
    public TableModelBuilder<TRow> Link(
        string header, Func<TRow, object?> text, Func<TRow, string?> href,
        Action<TableColumnOptions>? configure = null)
    {
        var options = new TableColumnOptions();
        configure?.Invoke(options);
        _columns.Add(new TableColumn<TRow>(header, CellFormat.Text, text, template: null, options, href));
        return this;
    }

    /// <summary>
    /// A member cell rendered by the Human view component, which shows the current name, so the
    /// column sorts by that rendered name. A null or empty id renders an em dash.
    /// </summary>
    public TableModelBuilder<TRow> Human(
        string header, Func<TRow, Guid?> userId, HumanLink link = HumanLink.Public,
        HumanLayout layout = HumanLayout.Text, Action<TableColumnOptions>? configure = null)
    {
        var options = new TableColumnOptions();
        configure?.Invoke(options);
        _columns.Add(new TableColumn<TRow>(header, CellFormat.Human, value: null, template: null, options,
            userId: userId, humanLayout: layout, humanLink: link));
        return this;
    }

    /// <summary>
    /// Razor-rendered cell. <paramref name="sortBy"/> gives client sorting a value other than
    /// the cell's visible text, such as a date or an amount. The value is written into the page
    /// as <c>data-sort-value</c>, so never pass one the page must not show, such as a stored name
    /// behind a <c>vc:human</c> cell: leave sortBy off and the column sorts by the rendered name.
    /// </summary>
    public TableModelBuilder<TRow> Template(
        string header, Func<TRow, IHtmlContent> template, Action<TableColumnOptions>? configure = null,
        Func<TRow, object?>? sortBy = null)
    {
        var options = new TableColumnOptions();
        configure?.Invoke(options);
        _columns.Add(new TableColumn<TRow>(header, CellFormat.Template, sortBy, template, options));
        return this;
    }

    public TableModelBuilder<TRow> RowHref(Func<TRow, string?> href) { _rowHref = href; return this; }
    public TableModelBuilder<TRow> RowCss(Func<TRow, string?> css) { _rowCss = css; return this; }

    /// <summary>Adds a <c>data-{name}</c> attribute to every row; call once per attribute.</summary>
    public TableModelBuilder<TRow> RowData(string name, Func<TRow, string?> value)
    {
        _rowData.Add((name, value));
        return this;
    }
    public TableModelBuilder<TRow> Empty(string message) { _emptyMessage = message; return this; }
    public TableModelBuilder<TRow> Id(string id) { _id = id; return this; }
    public TableModelBuilder<TRow> Css(string cssClass) { _extraCssClass = cssClass; return this; }

    public TableModelBuilder<TRow> SearchBox(
        string paramName = "search", string? value = null, string placeholder = "Search...")
    {
        _hasSearchBox = true;
        _searchParamName = paramName;
        _searchValue = value;
        _searchPlaceholder = placeholder;
        return this;
    }

    public TableModelBuilder<TRow> ServerMode(string? sortBy, bool sortDesc, PagerViewModel? pager = null)
    {
        _mode = TableMode.Server;
        _sortBy = sortBy;
        _sortDesc = sortDesc;
        _pager = pager;
        return this;
    }

    public TableModelBuilder<TRow> HiddenField(string name, string? value)
    {
        _hiddenFields[name] = value;
        return this;
    }

    public TableModelBuilder<TRow> ExtraFilters(Func<object?, IHtmlContent> content)
    {
        _extraFilterContent = content;
        return this;
    }

    public TableModelBuilder<TRow> Footer(params TableFooterCell[] cells)
    {
        _footerCells = cells;
        return this;
    }

    public ITableModel Build() => new BuiltTableModel(this);

    private sealed class BuiltTableModel(TableModelBuilder<TRow> b) : ITableModel
    {
        public TableMode Mode { get; } = b._mode;
        public IReadOnlyList<ITableColumn> Columns { get; } = b._columns;
        public IEnumerable<object> Rows { get; } = b._rows;
        public int RowCount { get; } = b._rows.Count;
        public string EmptyMessage { get; } = b._emptyMessage;
        public string? Id { get; } = b._id;
        public string? ExtraCssClass { get; } = b._extraCssClass;
        public bool HasSearchBox { get; } = b._hasSearchBox;
        public string SearchParamName { get; } = b._searchParamName;
        public string? SearchValue { get; } = b._searchValue;
        public string SearchPlaceholder { get; } = b._searchPlaceholder;
        public string? SortBy { get; } = b._sortBy;
        public bool SortDesc { get; } = b._sortDesc;
        public PagerViewModel? Pager { get; } = b._pager;
        public IReadOnlyList<TableFooterCell> FooterCells { get; } = b._footerCells;
        public IReadOnlyDictionary<string, string?> HiddenFields { get; } = b._hiddenFields;
        public Func<object?, IHtmlContent>? ExtraFilterContent { get; } = b._extraFilterContent;

        private readonly Func<TRow, string?>? _rowHref = b._rowHref;
        private readonly Func<TRow, string?>? _rowCss = b._rowCss;
        private readonly IReadOnlyList<(string Name, Func<TRow, string?> Value)> _rowData = b._rowData;

        public string? RowHref(object row) => _rowHref?.Invoke((TRow)row);
        public string? RowCss(object row) => _rowCss?.Invoke((TRow)row);

        public IHtmlContent RowData(object row)
        {
            if (_rowData.Count == 0)
                return HtmlString.Empty;
            var attributes = _rowData.Select(d =>
                $" data-{HtmlEncoder.Default.Encode(d.Name)}=\"{HtmlEncoder.Default.Encode(d.Value((TRow)row) ?? string.Empty)}\"");
            return new HtmlString(string.Concat(attributes));
        }
    }
}
