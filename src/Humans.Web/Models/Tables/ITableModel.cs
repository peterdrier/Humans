using Microsoft.AspNetCore.Html;

namespace Humans.Web.Models.Tables;

/// <summary>
/// Non-generic render contract consumed by <c>Views/Shared/_Table.cshtml</c>.
/// Built via <see cref="TableModel.For{TRow}"/>; see
/// docs/superpowers/specs/2026-06-10-table-component-design.md.
/// </summary>
public interface ITableModel
{
    TableMode Mode { get; }
    IReadOnlyList<ITableColumn> Columns { get; }
    IEnumerable<object> Rows { get; }
    int RowCount { get; }
    string EmptyMessage { get; }
    string? Id { get; }

    /// <summary>Extra classes appended to the canonical "table table-sm table-hover mb-0".</summary>
    string? ExtraCssClass { get; }

    bool HasSearchBox { get; }
    string SearchParamName { get; }
    string? SearchValue { get; }
    string SearchPlaceholder { get; }

    /// <summary>Server mode: current sort state, echoed as hidden form fields and toggled by header links.</summary>
    string? SortBy { get; }
    bool SortDesc { get; }

    PagerViewModel? Pager { get; }
    IReadOnlyList<TableFooterCell> FooterCells { get; }

    /// <summary>Server mode: extra hidden form fields (e.g. pageSize) the filter form must preserve.</summary>
    IReadOnlyDictionary<string, string?> HiddenFields { get; }

    /// <summary>Server mode: caller-supplied extra filter controls rendered inside the GET form (invoked with null).</summary>
    Func<object?, IHtmlContent>? ExtraFilterContent { get; }

    string? RowHref(object row);
    string? RowCss(object row);
}

public sealed record TableFooterCell(string Text, string? CssClass = null);

public interface ITableColumn
{
    string Header { get; }
    CellFormat Format { get; }

    /// <summary>Server mode: the sortBy query value for this column; null = not server-sortable.</summary>
    string? SortKey { get; }

    /// <summary>Client mode: whether the site.js sort engine binds to this header. Default true.</summary>
    bool ClientSortable { get; }

    /// <summary>data-sort-type for the client engine: "auto", "text", or "number".</summary>
    string SortType { get; }

    ColumnFilterKind FilterKind { get; }
    string? FilterParamName { get; }
    string? FilterValue { get; }
    IReadOnlyList<string> FilterOptions { get; }

    string? CellCssClass { get; }
    string? HeaderCssClass { get; }

    /// <summary>Fully formatted cell content for one row.</summary>
    IHtmlContent Cell(object row);

    /// <summary>data-sort-value emitted on the td so dates/money sort correctly client-side; null = omit.</summary>
    string? SortValue(object row);
}
