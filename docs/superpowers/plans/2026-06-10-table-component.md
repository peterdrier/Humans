# Table Component Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the column-model table component from `docs/superpowers/specs/2026-06-10-table-component-design.md` (Phase 1): `TableModel<TRow>` + builder + formatters, the `_Table.cshtml` renderer, a `site.js` client-filter engine, a WidgetGallery demo, and three flagship view conversions.

**Architecture:** New `Humans.Web/Models/Tables/` namespace holds a typed column model (`TableModel<TRow>`, `TableColumn<TRow>`, fluent builder) erased to non-generic `ITableModel`/`ITableColumn` consumed by one shared partial `Views/Shared/_Table.cshtml`. Client mode reuses the existing `site.js` sort engine via `data-sortable-table` attributes plus a new declarative filter engine; server mode emits `sortBy`/`sortDesc` links (query-string rebuild, same technique as `_Pager.cshtml`), a GET filter form matching existing controller params, and reuses `_Pager.cshtml`.

**Tech Stack:** ASP.NET Core MVC Razor (no Blazor), Bootstrap 5.3.3, vanilla JS in `wwwroot/js/site.js`, NodaTime `Instant` dates via `DateTimeDisplayExtensions`, xunit + AwesomeAssertions + `[HumansFact]` in `tests/Humans.Web.Tests`.

**Working directory:** `H:\source\Humans\.worktrees\table-component` (branch `feat/table-component`). ALL paths below are relative to this worktree. Run all commands from this directory. Build: `dotnet build Humans.slnx -v quiet` · Test: `dotnet test Humans.slnx -v quiet` (or `--filter` as shown).

**Conventions that bind every task:**
- Never search or edit under `H:\source\Humans\src` (the main checkout) — only under the worktree.
- Tests use `[HumansFact]` (not `[Fact]`), `AwesomeAssertions` (`.Should()`), file-scoped namespaces.
- Currency is bare `N2` (current culture), **no currency symbol** — Peter's explicit decision.
- No burn-down guard / analyzer in this phase — explicitly deferred.

---

### Task 1: Core contracts — enums, footer cell, `ITableModel`, `ITableColumn`

**Files:**
- Create: `src/Humans.Web/Models/Tables/TableEnums.cs`
- Create: `src/Humans.Web/Models/Tables/ITableModel.cs`

- [ ] **Step 1: Create `TableEnums.cs`**

```csharp
namespace Humans.Web.Models.Tables;

public enum TableMode
{
    /// <summary>All rows rendered; sorting/filtering happen in the browser (site.js). The default.</summary>
    Client,

    /// <summary>Sorting/filtering/paging round-trip via query params; renderer emits sort links + GET filter form.</summary>
    Server,
}

public enum CellFormat
{
    Text,
    Date,
    DateTime,
    Currency,
    Number,
    EnumBadge,
    BoolIcon,
    Template,
}

public enum ColumnFilterKind
{
    None,
    Text,
    Select,
}
```

- [ ] **Step 2: Create `ITableModel.cs`**

```csharp
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
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Humans.Web -v quiet`
Expected: Build succeeded (warnings about unused types are fine; there should be none).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/Tables/
git commit -m "feat(tables): core table component contracts (ITableModel, ITableColumn, enums)"
```

---

### Task 2: `EnumBadgeMap` — central enum→badge-class registry (TDD)

**Files:**
- Create: `src/Humans.Web/Models/Tables/EnumBadgeMap.cs`
- Test: `tests/Humans.Web.Tests/Models/Tables/EnumBadgeMapTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Humans.Domain.Enums;
using Humans.Web.Models.Tables;

namespace Humans.Web.Tests.Models.Tables;

public class EnumBadgeMapTests
{
    [HumansFact]
    public void Mapped_enum_values_get_their_registered_badge_class()
    {
        EnumBadgeMap.For(TicketAttendeeStatus.Valid).Should().Be("bg-success");
        EnumBadgeMap.For(TicketAttendeeStatus.CheckedIn).Should().Be("bg-info");
        EnumBadgeMap.For(TicketAttendeeStatus.Void).Should().Be("bg-danger");
    }

    [HumansFact]
    public void Unmapped_enum_values_fall_back_to_secondary()
    {
        EnumBadgeMap.For(StoreOrderCounterpartyType.Team).Should().Be("bg-secondary");
    }
}
```

(If `TicketAttendeeStatus` / `StoreOrderCounterpartyType` live in a different namespace than `Humans.Domain.Enums`, follow the compiler — they are the enums used by `Views/Ticket/Attendees.cshtml` and `Views/StoreAdmin/Summary.cshtml`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter EnumBadgeMapTests`
Expected: FAIL — `EnumBadgeMap` does not exist (compile error).

- [ ] **Step 3: Create `EnumBadgeMap.cs`**

```csharp
using Humans.Domain.Enums;

namespace Humans.Web.Models.Tables;

/// <summary>
/// Central enum-value → Bootstrap badge class registry for <see cref="CellFormat.EnumBadge"/> columns.
/// Views stop owning color decisions: add new mappings here, never inline in a view.
/// Unmapped values render as bg-secondary.
/// </summary>
public static class EnumBadgeMap
{
    private static readonly Dictionary<Enum, string> Map = new()
    {
        [TicketAttendeeStatus.Valid] = "bg-success",
        [TicketAttendeeStatus.CheckedIn] = "bg-info",
        [TicketAttendeeStatus.Void] = "bg-danger",
    };

    public static string For(Enum value) => Map.GetValueOrDefault(value, "bg-secondary");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter EnumBadgeMapTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/Tables/EnumBadgeMap.cs tests/Humans.Web.Tests/Models/Tables/
git commit -m "feat(tables): EnumBadgeMap central badge-class registry"
```

---

### Task 3: `TableColumn<TRow>` — formatters and sort values (TDD)

**Files:**
- Create: `src/Humans.Web/Models/Tables/TableColumn.cs`
- Test: `tests/Humans.Web.Tests/Models/Tables/TableColumnTests.cs`

`TableColumn<TRow>` is internal-ish plumbing (public class, constructed only by the builder in Task 4, but testable directly). `Cell()` dispatches on `CellFormat`; `SortValue()` emits machine-sortable values for Date/DateTime/Currency/Number.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Domain.Enums;
using Humans.Web.Models.Tables;
using Microsoft.AspNetCore.Html;
using NodaTime;

namespace Humans.Web.Tests.Models.Tables;

public class TableColumnTests
{
    private sealed class Row
    {
        public string? Name { get; init; }
        public decimal Amount { get; init; }
        public Instant? When { get; init; }
        public TicketAttendeeStatus Status { get; init; }
        public bool Flag { get; init; }
    }

    private static string Render(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    private static TableColumn<Row> Col(CellFormat format, Func<Row, object?> value) =>
        new("H", format, value, template: null, new TableColumnOptions());

    [HumansFact]
    public void Text_encodes_value_and_renders_null_as_muted_emdash()
    {
        Render(Col(CellFormat.Text, r => r.Name).Cell(new Row { Name = "a<b" }))
            .Should().Contain("a").And.Contain("&lt;").And.Contain("b");
        Render(Col(CellFormat.Text, r => r.Name).Cell(new Row { Name = null }))
            .Should().Be("""<span class="text-muted">—</span>""");
    }

    [HumansFact]
    public void Currency_renders_n2_with_no_symbol_and_invariant_sort_value()
    {
        var col = Col(CellFormat.Currency, r => r.Amount);
        var row = new Row { Amount = 1234.5m };

        Render(col.Cell(row)).Should().Be(1234.5m.ToString("N2"));
        Render(col.Cell(row)).Should().NotContain("€").And.NotContain("$");
        col.SortValue(row).Should().Be("1234.5");
        col.SortType.Should().Be("number");
    }

    [HumansFact]
    public void Date_uses_shared_display_extension_and_unix_ms_sort_value()
    {
        var instant = Instant.FromUtc(2026, 6, 10, 12, 0);
        var col = Col(CellFormat.Date, r => r.When);
        var row = new Row { When = instant };

        Render(col.Cell(row)).Should().Be(HtmlEncoder.Default.Encode(instant.ToDate()));
        col.SortValue(row).Should().Be(instant.ToUnixTimeMilliseconds().ToString());
        Render(col.Cell(new Row { When = null })).Should().Contain("—");
    }

    [HumansFact]
    public void EnumBadge_wraps_value_in_registered_badge()
    {
        var col = Col(CellFormat.EnumBadge, r => r.Status);
        Render(col.Cell(new Row { Status = TicketAttendeeStatus.Valid }))
            .Should().Be("""<span class="badge bg-success">Valid</span>""");
    }

    [HumansFact]
    public void BoolIcon_renders_check_or_muted_emdash()
    {
        var col = Col(CellFormat.BoolIcon, r => r.Flag);
        Render(col.Cell(new Row { Flag = true })).Should().Contain("fa-check");
        Render(col.Cell(new Row { Flag = false })).Should().Contain("text-muted");
    }

    [HumansFact]
    public void Template_invokes_the_razor_delegate_with_the_typed_row()
    {
        var col = new TableColumn<Row>("H", CellFormat.Template, value: null,
            template: r => new HtmlString($"<b>{r.Name}</b>"), new TableColumnOptions());
        Render(col.Cell(new Row { Name = "x" })).Should().Be("<b>x</b>");
        col.SortValue(new Row { Name = "x" }).Should().BeNull();
    }
}
```

(`Instant.ToDate()` falls back to UTC outside an HTTP request — `DateTimeDisplayExtensions.GetCurrentUserTimeZone()` returns `DateTimeZone.Utc` when no context — so the test is deterministic. Bring `Humans.Web.Extensions` into scope with a `using` if needed.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter TableColumnTests`
Expected: FAIL — `TableColumn<>` / `TableColumnOptions` do not exist.

- [ ] **Step 3: Create `TableColumn.cs`** (includes `TableColumnOptions`)

```csharp
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
    internal bool ClientSortableValue { get; private set; } = true;
    internal ColumnFilterKind FilterKindValue { get; private set; }
    internal string? FilterParamNameValue { get; private set; }
    internal string? FilterValueValue { get; private set; }
    internal IReadOnlyList<string> FilterOptionsValue { get; private set; } = [];
    internal string? CellCssValue { get; private set; }
    internal string? HeaderCssValue { get; private set; }

    /// <summary>Server mode: make this column sortable under the given sortBy key.</summary>
    public TableColumnOptions Sort(string serverKey) { SortKeyValue = serverKey; return this; }

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
            CellFormat.Currency => Encoded(ToDecimal(raw).ToString("N2")),
            CellFormat.Number => Encoded(ToDecimal(raw).ToString("#,##0.##")),
            CellFormat.EnumBadge => Badge((Enum)raw),
            CellFormat.BoolIcon => (bool)raw ? CheckContent : NullContent,
            _ => Encoded(raw.ToString() ?? string.Empty),
        };
    }

    public string? SortValue(object row)
    {
        if (value is null || SortType != "number")
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
```

Note for the test in Step 1: the `Text_encodes…` assertion uses `.Contain("&lt;")` rather than an exact string because `HtmlEncoder` may encode `—` as an entity; if the null-case exact assertion fails on encoding, relax it to `.Should().Contain("text-muted").And.Contain("—")` — `NullContent` is a pre-built `HtmlString`, so it writes literally and the exact assertion should hold.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter TableColumnTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/Tables/TableColumn.cs tests/Humans.Web.Tests/Models/Tables/TableColumnTests.cs
git commit -m "feat(tables): TableColumn formatters — date/currency/number/enum-badge/bool-icon/template"
```

---

### Task 4: `TableModel<TRow>` + `TableModelBuilder<TRow>` (TDD)

**Files:**
- Create: `src/Humans.Web/Models/Tables/TableModel.cs`
- Test: `tests/Humans.Web.Tests/Models/Tables/TableModelBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using AwesomeAssertions;
using Humans.Web.Models;
using Humans.Web.Models.Tables;
using Microsoft.AspNetCore.Html;

namespace Humans.Web.Tests.Models.Tables;

public class TableModelBuilderTests
{
    private sealed class Row
    {
        public string Name { get; init; } = "";
        public decimal Amount { get; init; }
    }

    private static readonly List<Row> Rows = [new() { Name = "a", Amount = 1m }, new() { Name = "b", Amount = 2m }];

    [HumansFact]
    public void Builder_preserves_column_order_headers_and_formats()
    {
        var table = TableModel.For(Rows)
            .Column("Name", r => r.Name)
            .Column("Amount", r => r.Amount, c => c.Currency().End())
            .Template("Actions", r => new HtmlString("<a>x</a>"))
            .Build();

        table.Columns.Select(c => c.Header).Should().Equal("Name", "Amount", "Actions");
        table.Columns[1].Format.Should().Be(CellFormat.Currency);
        table.Columns[1].CellCssClass.Should().Be("text-end");
        table.Columns[2].Format.Should().Be(CellFormat.Template);
        table.RowCount.Should().Be(2);
        table.Mode.Should().Be(TableMode.Client);
        table.EmptyMessage.Should().Be("No results");
    }

    [HumansFact]
    public void ServerMode_captures_sort_state_pager_and_hidden_fields()
    {
        var pager = new PagerViewModel(totalPages: 3, currentPage: 1, action: "Index");
        var table = TableModel.For(Rows)
            .Column("Name", r => r.Name, c => c.Sort("name"))
            .SearchBox("search", "abc", "Find...")
            .ServerMode("name", sortDesc: true, pager)
            .HiddenField("pageSize", "25")
            .Empty("Nothing here")
            .Build();

        table.Mode.Should().Be(TableMode.Server);
        table.SortBy.Should().Be("name");
        table.SortDesc.Should().BeTrue();
        table.Pager.Should().BeSameAs(pager);
        table.HiddenFields.Should().ContainKey("pageSize").WhoseValue.Should().Be("25");
        table.HasSearchBox.Should().BeTrue();
        table.SearchValue.Should().Be("abc");
        table.SearchPlaceholder.Should().Be("Find...");
        table.EmptyMessage.Should().Be("Nothing here");
        table.Columns[0].SortKey.Should().Be("name");
    }

    [HumansFact]
    public void RowHref_and_RowCss_delegate_to_the_typed_lambdas()
    {
        var table = TableModel.For(Rows)
            .Column("Name", r => r.Name)
            .RowHref(r => $"/x/{r.Name}")
            .RowCss(r => r.Amount > 1m ? "table-warning" : null)
            .Build();

        table.RowHref(Rows[0]).Should().Be("/x/a");
        table.RowCss(Rows[0]).Should().BeNull();
        table.RowCss(Rows[1]).Should().Be("table-warning");
    }

    [HumansFact]
    public void Footer_cells_are_carried_through()
    {
        var table = TableModel.For(Rows)
            .Column("Name", r => r.Name)
            .Footer(new TableFooterCell("Total"), new TableFooterCell("3.00", "text-end"))
            .Build();

        table.FooterCells.Should().HaveCount(2);
        table.FooterCells[1].CssClass.Should().Be("text-end");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter TableModelBuilderTests`
Expected: FAIL — `TableModel` / `TableModelBuilder<>` do not exist.

- [ ] **Step 3: Create `TableModel.cs`**

```csharp
using Microsoft.AspNetCore.Html;

namespace Humans.Web.Models.Tables;

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

    internal TableModelBuilder(IReadOnlyList<TRow> rows) => _rows = rows;

    public TableModelBuilder<TRow> Column(
        string header, Func<TRow, object?> value, Action<TableColumnOptions>? configure = null)
    {
        var options = new TableColumnOptions();
        configure?.Invoke(options);
        _columns.Add(new TableColumn<TRow>(header, CellFormat.Text, value, template: null, options));
        return this;
    }

    public TableModelBuilder<TRow> Template(
        string header, Func<TRow, IHtmlContent> template, Action<TableColumnOptions>? configure = null)
    {
        var options = new TableColumnOptions();
        configure?.Invoke(options);
        _columns.Add(new TableColumn<TRow>(header, CellFormat.Template, value: null, template, options));
        return this;
    }

    public TableModelBuilder<TRow> RowHref(Func<TRow, string?> href) { _rowHref = href; return this; }
    public TableModelBuilder<TRow> RowCss(Func<TRow, string?> css) { _rowCss = css; return this; }
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

        public string? RowHref(object row) => _rowHref?.Invoke((TRow)row);
        public string? RowCss(object row) => _rowCss?.Invoke((TRow)row);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter TableModelBuilderTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full Web tests to catch regressions, then commit**

Run: `dotnet test tests/Humans.Web.Tests -v quiet`
Expected: all pass.

```bash
git add src/Humans.Web/Models/Tables/TableModel.cs tests/Humans.Web.Tests/Models/Tables/TableModelBuilderTests.cs
git commit -m "feat(tables): TableModel fluent builder with client/server modes"
```

---

### Task 5: `_Table.cshtml` — the single renderer

**Files:**
- Create: `src/Humans.Web/Views/Shared/_Table.cshtml`

No unit test (Razor partial); verified by build + every later task's converted view. Renders: optional server GET filter form, optional client search box, the canonical table, footer, pager.

- [ ] **Step 1: Create `_Table.cshtml`**

```cshtml
@using Humans.Web.Models.Tables
@using Microsoft.AspNetCore.WebUtilities
@model ITableModel
@{
    var cols = Model.Columns;
    var clientSort = Model.Mode == TableMode.Client && cols.Any(c => c.ClientSortable);
    var anyFilter = cols.Any(c => c.FilterKind != ColumnFilterKind.None);
    var tableClass = string.IsNullOrEmpty(Model.ExtraCssClass)
        ? "table table-sm table-hover mb-0"
        : $"table table-sm table-hover mb-0 {Model.ExtraCssClass}";

    // Server-mode sort link: preserve every current query param except page,
    // toggle sortDesc when re-clicking the active column. Same technique as _Pager.cshtml.
    string SortHref(string key)
    {
        var query = Context.Request.Query
            .Where(p => !string.Equals(p.Key, "page", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var isActive = string.Equals(Model.SortBy, key, StringComparison.OrdinalIgnoreCase);
        query["sortBy"] = key;
        query["sortDesc"] = (isActive && !Model.SortDesc).ToString();
        return QueryHelpers.AddQueryString(Context.Request.Path, query!);
    }
}

@if (Model.Mode == TableMode.Server && (Model.HasSearchBox || anyFilter || Model.ExtraFilterContent is not null))
{
    <form method="get" class="row g-2 mb-3">
        @if (Model.HasSearchBox)
        {
            <div class="col-md-3">
                <input type="text" name="@Model.SearchParamName" value="@Model.SearchValue"
                       class="form-control" placeholder="@Model.SearchPlaceholder" />
            </div>
        }
        @foreach (var col in cols.Where(c => c.FilterKind == ColumnFilterKind.Select))
        {
            <div class="col-md-2">
                <select name="@col.FilterParamName" class="form-select js-auto-submit" aria-label="Filter by @col.Header">
                    <option value="">All</option>
                    @foreach (var opt in col.FilterOptions)
                    {
                        <option value="@opt" selected="@(col.FilterValue == opt ? "selected" : null)">@opt</option>
                    }
                </select>
            </div>
        }
        @foreach (var col in cols.Where(c => c.FilterKind == ColumnFilterKind.Text))
        {
            <div class="col-md-2">
                <input type="text" name="@col.FilterParamName" value="@col.FilterValue"
                       class="form-control" placeholder="@col.Header" />
            </div>
        }
        @if (Model.ExtraFilterContent is not null)
        {
            @Model.ExtraFilterContent(null)
        }
        <div class="col-auto">
            <button type="submit" class="btn btn-primary"><i class="fa-solid fa-search"></i> Filter</button>
        </div>
        @if (Model.SortBy is not null)
        {
            <input type="hidden" name="sortBy" value="@Model.SortBy" />
            <input type="hidden" name="sortDesc" value="@Model.SortDesc" />
        }
        @foreach (var hidden in Model.HiddenFields)
        {
            <input type="hidden" name="@hidden.Key" value="@hidden.Value" />
        }
    </form>
}

<div class="table-component">
    @if (Model.Mode == TableMode.Client && Model.HasSearchBox)
    {
        <input type="search" class="form-control form-control-sm mb-2" style="max-width: 20rem;"
               placeholder="@Model.SearchPlaceholder" data-table-search />
    }
    <div class="table-responsive">
        <table id="@Model.Id" class="@tableClass" data-sortable-table="@(clientSort ? "" : null)">
            <thead>
                <tr>
                    @foreach (var col in cols)
                    {
                        if (Model.Mode == TableMode.Client && col.ClientSortable)
                        {
                            <th scope="col" class="@col.HeaderCssClass" data-sort data-sort-type="@col.SortType">
                                @col.Header <span class="sort-indicator" aria-hidden="true"></span>
                            </th>
                        }
                        else if (Model.Mode == TableMode.Server && col.SortKey is not null)
                        {
                            var isActive = string.Equals(Model.SortBy, col.SortKey, StringComparison.OrdinalIgnoreCase);
                            <th scope="col" class="@col.HeaderCssClass"
                                aria-sort="@(isActive ? (Model.SortDesc ? "descending" : "ascending") : null)">
                                <a href="@SortHref(col.SortKey)">@col.Header</a>@(isActive ? Model.SortDesc ? " ▼" : " ▲" : null)
                            </th>
                        }
                        else
                        {
                            <th scope="col" class="@col.HeaderCssClass">@col.Header</th>
                        }
                    }
                </tr>
                @if (Model.Mode == TableMode.Client && anyFilter)
                {
                    <tr data-table-filter-row>
                        @for (var i = 0; i < cols.Count; i++)
                        {
                            <th class="@cols[i].HeaderCssClass">
                                @if (cols[i].FilterKind == ColumnFilterKind.Text)
                                {
                                    <input type="search" class="form-control form-control-sm fw-normal"
                                           data-filter-col="@i" placeholder="Filter..." aria-label="Filter @cols[i].Header" />
                                }
                                else if (cols[i].FilterKind == ColumnFilterKind.Select)
                                {
                                    <select class="form-select form-select-sm fw-normal" data-filter-col="@i"
                                            aria-label="Filter @cols[i].Header">
                                        <option value="">All</option>
                                        @foreach (var opt in cols[i].FilterOptions)
                                        {
                                            <option value="@opt">@opt</option>
                                        }
                                    </select>
                                }
                            </th>
                        }
                    </tr>
                }
            </thead>
            <tbody>
                @foreach (var row in Model.Rows)
                {
                    <tr data-href="@Model.RowHref(row)" class="@Model.RowCss(row)">
                        @foreach (var col in cols)
                        {
                            <td class="@col.CellCssClass" data-sort-value="@col.SortValue(row)">@col.Cell(row)</td>
                        }
                    </tr>
                }
                @if (Model.RowCount == 0)
                {
                    <tr><td colspan="@cols.Count" class="text-muted text-center">@Model.EmptyMessage</td></tr>
                }
            </tbody>
            @if (Model.FooterCells.Count > 0)
            {
                <tfoot class="table-group-divider">
                    <tr class="fw-semibold">
                        @foreach (var cell in Model.FooterCells)
                        {
                            <td class="@cell.CssClass">@cell.Text</td>
                        }
                    </tr>
                </tfoot>
            }
        </table>
    </div>
</div>

@if (Model.Pager is not null)
{
    <partial name="_Pager" model="Model.Pager" />
}
```

Razor omits attributes whose value is `null` (`data-href`, `class`, `aria-sort`, `id`, `data-sortable-table`), so empty rows render clean. `data-sortable-table="@(clientSort ? "" : null)"` renders the bare attribute in client mode and nothing in server mode.

- [ ] **Step 2: Build to verify the partial compiles**

Run: `dotnet build src/Humans.Web -v quiet`
Expected: Build succeeded. (Razor views compile at build time in this project; if a Razor error appears, fix syntax — common gotcha: `@(isActive ? Model.SortDesc ? " ▼" : " ▲" : null)` may need parentheses: `@(isActive ? (Model.SortDesc ? " ▼" : " ▲") : null)`.)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/_Table.cshtml
git commit -m "feat(tables): _Table.cshtml shared renderer for client and server modes"
```

---

### Task 6: `site.js` — declarative client-side filter engine

**Files:**
- Modify: `src/Humans.Web/wwwroot/js/site.js` (insert AFTER the sort engine IIFE that ends at the line `})();` around line 146 — directly before the `// Timezone detection` comment)

The existing sort engine is untouched. The new engine binds per `.table-component` wrapper (emitted by `_Table.cshtml`), so no IDs are needed.

- [ ] **Step 1: Append the filter engine to `site.js`**

```js
// Declarative client-side table filtering (companion to data-sortable-table).
// _Table.cshtml emits a .table-component wrapper; inside it:
//   input[data-table-search]      — global contains-search across all cells
//   [data-filter-col="<index>"]   — per-column filter (input = contains, select = exact)
// Selects rendered with no options are auto-populated from distinct column values.
(function () {
    function norm(value) {
        return (value || '').trim().toLowerCase();
    }

    function applyFilters(table, search, filters) {
        var term = norm(search && search.value);
        Array.from(table.tBodies[0].rows).forEach(function (row) {
            var visible = !term || norm(row.textContent).indexOf(term) !== -1;
            filters.forEach(function (filter) {
                if (!visible) return;
                var wanted = norm(filter.input.value);
                if (!wanted) return;
                var cell = row.cells[filter.col];
                var text = norm(cell && cell.textContent);
                visible = filter.exact ? text === wanted : text.indexOf(wanted) !== -1;
            });
            row.style.display = visible ? '' : 'none';
        });
    }

    document.querySelectorAll('.table-component').forEach(function (root) {
        var table = root.querySelector('table');
        if (!table || !table.tBodies[0]) return;

        var search = root.querySelector('input[data-table-search]');
        var filters = [];

        root.querySelectorAll('[data-filter-col]').forEach(function (input) {
            var col = parseInt(input.dataset.filterCol, 10);
            if (isNaN(col)) return;
            var exact = input.tagName === 'SELECT';

            if (exact && input.options.length <= 1) {
                var seen = {};
                Array.from(table.tBodies[0].rows).forEach(function (row) {
                    var text = (row.cells[col] ? row.cells[col].textContent : '').trim();
                    if (text && text !== '—' && !seen[text]) {
                        seen[text] = true;
                        var option = document.createElement('option');
                        option.value = text;
                        option.textContent = text;
                        input.appendChild(option);
                    }
                });
            }

            filters.push({ input: input, col: col, exact: exact });
            input.addEventListener(exact ? 'change' : 'input', function () {
                applyFilters(table, search, filters);
            });
        });

        if (search) {
            var debounce;
            search.addEventListener('input', function () {
                clearTimeout(debounce);
                debounce = setTimeout(function () { applyFilters(table, search, filters); }, 150);
            });
        }
    });
})();
```

- [ ] **Step 2: Verify placement and syntax**

Run: `node --check src/Humans.Web/wwwroot/js/site.js`
Expected: no output (exit 0). If `node` is unavailable, build + load any page and check the browser console instead.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/wwwroot/js/site.js
git commit -m "feat(tables): client-side table filter engine in site.js"
```

---

### Task 7: WidgetGallery demo entry

**Files:**
- Modify: `src/Humans.Web/Controllers/WidgetGalleryController.cs` (view model at line ~219, `Index` action model construction at line ~42)
- Modify: `src/Humans.Web/Views/WidgetGallery/Index.cshtml` (add a "Tables" `wg-section` following the existing `wg-card` markup pattern, and a TOC link in the `.wg-toc` block)

- [ ] **Step 1: Add demo rows to `WidgetGalleryViewModel`**

In `WidgetGalleryController.cs`, add to the `WidgetGalleryViewModel` class (line ~219):

```csharp
public List<TableDemoRow> SampleTableRows { get; set; } = [];
```

And add below the view model class in the same file:

```csharp
public sealed class TableDemoRow
{
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public NodaTime.Instant JoinedAt { get; set; }
    public Humans.Domain.Enums.TicketAttendeeStatus Status { get; set; }
    public bool IsVip { get; set; }
}
```

- [ ] **Step 2: Populate sample rows in the `Index` action**

Inside the `new WidgetGalleryViewModel { ... }` initializer add:

```csharp
SampleTableRows =
[
    new() { Name = "Sparkle", Amount = 120.50m, JoinedAt = NodaTime.SystemClock.Instance.GetCurrentInstant().Minus(NodaTime.Duration.FromDays(400)), Status = Humans.Domain.Enums.TicketAttendeeStatus.Valid, IsVip = true },
    new() { Name = "Embers", Amount = 95.00m, JoinedAt = NodaTime.SystemClock.Instance.GetCurrentInstant().Minus(NodaTime.Duration.FromDays(120)), Status = Humans.Domain.Enums.TicketAttendeeStatus.CheckedIn, IsVip = false },
    new() { Name = "Dusty", Amount = 240.00m, JoinedAt = NodaTime.SystemClock.Instance.GetCurrentInstant().Minus(NodaTime.Duration.FromDays(30)), Status = Humans.Domain.Enums.TicketAttendeeStatus.Void, IsVip = false },
    new() { Name = "Nova", Amount = 95.00m, JoinedAt = NodaTime.SystemClock.Instance.GetCurrentInstant().Minus(NodaTime.Duration.FromDays(10)), Status = Humans.Domain.Enums.TicketAttendeeStatus.Valid, IsVip = false },
],
```

(Match the file's existing `using` style — add `using NodaTime;` / `using Humans.Domain.Enums;` at the top and drop the qualifications if cleaner.)

- [ ] **Step 3: Add the gallery card to `Views/WidgetGallery/Index.cshtml`**

Add a TOC link inside the `.wg-toc` nav: `<a href="#wg-tables">Tables</a>` (match neighbors). Then add a new section (copy the structure of an existing `wg-section`; the `kind-pa` chip class is for partials):

```cshtml
<section class="wg-section" id="wg-tables">
    <h2>Tables</h2>
    <div class="wg-card">
        <div class="wg-card-header">
            <div>
                <span class="wg-card-name">_Table.cshtml (TableModel)</span>
                <span class="wg-card-kind kind-pa">Partial</span>
            </div>
            <span class="wg-card-path">src/Humans.Web/Views/Shared/_Table.cshtml · src/Humans.Web/Models/Tables/</span>
        </div>
        <div class="wg-card-note">
            Column-model table component — the single authority for list tables. Sorting, filtering,
            and formatting are per-column config. Client mode shown here (sort: click headers;
            filter row + search box). Server mode (query-param sort links, GET filter form, _Pager)
            is live on /Ticket/Attendees. Spec: docs/superpowers/specs/2026-06-10-table-component-design.md
        </div>
        <div class="wg-card-example">
            @{
                var demoTable = TableModel.For(Model.SampleTableRows)
                    .Column("Name", r => r.Name, c => c.TextFilter())
                    .Column("Joined", r => r.JoinedAt, c => c.Date())
                    .Column("Amount", r => r.Amount, c => c.Currency().End())
                    .Column("Status", r => r.Status, c => c.EnumBadge().SelectFilter())
                    .Column("VIP", r => r.IsVip, c => c.BoolIcon())
                    .SearchBox(placeholder: "Search demo rows...")
                    .Footer(new TableFooterCell("Total"), new TableFooterCell(""),
                            new TableFooterCell(Model.SampleTableRows.Sum(r => r.Amount).ToString("N2"), "text-end"),
                            new TableFooterCell(""), new TableFooterCell(""))
                    .Build();
            }
            <partial name="_Table" model="demoTable" />
        </div>
    </div>
</section>
```

Add `@using Humans.Web.Models.Tables` at the top of the view (with the existing `@using` lines).

- [ ] **Step 4: Build and verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/WidgetGalleryController.cs src/Humans.Web/Views/WidgetGallery/Index.cshtml
git commit -m "feat(tables): WidgetGallery demo card for the table component"
```

---

### Task 8: Flagship conversion 1 — `ProfileAdmin/EmailProblems` (client mode, template-heavy)

**Files:**
- Modify: `src/Humans.Web/Views/ProfileAdmin/EmailProblems.cshtml`

Three small tables; the win is canonical markup + dead inline divergence (`class="table"`, `class="table table-sm"` in one view). Keep the surrounding headings, prose, empty-state `<p class="text-success">` blocks, the backfill form, and the `data-confirm` script EXACTLY as they are — only the three `<table>...</table>` blocks are replaced.

- [ ] **Step 1: Add the using and replace the three tables**

Add to the top of the view (after the existing `@using` lines):

```cshtml
@using Humans.Web.Models.Tables
@using Microsoft.AspNetCore.Html
```

Replace the first table (Single-user issues, currently lines ~26-41) with:

```cshtml
    @{
        Func<SingleUserIssueRow, IHtmlContent> issueUserCell =
            @<text><vc:human user-id="@item.UserId" link="Admin" /></text>;
        Func<SingleUserIssueRow, IHtmlContent> issueActionCell =
            @<text><a class="btn btn-sm btn-secondary" href="/Profile/@item.UserId/Admin/Emails">Open emails ▸</a></text>;
        var singleUserTable = TableModel.For(Model.SingleUserIssues)
            .Template("User", issueUserCell)
            .Column("Problems", r => string.Join(", ", r.ProblemSummaries))
            .Template("", issueActionCell, c => c.NoSort().End())
            .Build();
    }
    <partial name="_Table" model="singleUserTable" />
```

Replace the second table (Legacy emails, currently lines ~63-74) with:

```cshtml
    @{
        Func<LegacyEmailRow, IHtmlContent> legacyUserCell =
            @<text><vc:human user-id="@item.UserId" link="Admin" /></text>;
        Func<LegacyEmailRow, IHtmlContent> legacyEmailCell =
            @<text><code>@item.LegacyEmail</code></text>;
        var legacyTable = TableModel.For(Model.LegacyEmailRows)
            .Template("User", legacyUserCell)
            .Template("Legacy User.Email", legacyEmailCell)
            .Build();
    }
    <partial name="_Table" model="legacyTable" />
```

Replace the third table (System-level issues, currently lines ~84-113) with:

```cshtml
    @{
        Func<SystemLevelIssueRow, IHtmlContent> systemActionCell = @<text>
            @if (item.Kind == EmailProblemKind.OrphanUserEmail)
            {
                <form method="post" asp-action="DeleteOrphanEmail" class="d-inline">
                    @Html.AntiForgeryToken()
                    <input type="hidden" name="emailId" value="@item.UserEmailId" />
                    <button type="submit" class="btn btn-sm btn-danger"
                            data-confirm="Delete orphan UserEmail row? This is irreversible.">Delete row ✕</button>
                </form>
            }
            else if (item.Kind == EmailProblemKind.GhostExternalLogins)
            {
                <a class="btn btn-sm btn-outline-secondary"
                   asp-controller="Profile"
                   asp-action="AdminEmails"
                   asp-route-id="@item.UserId"
                   asp-fragment="external-logins">Investigate ▸</a>
            }
        </text>;
        var systemTable = TableModel.For(Model.SystemLevelIssues)
            .Column("Issue", r => r.Detail)
            .Template("", systemActionCell, c => c.NoSort().End())
            .Build();
    }
    <partial name="_Table" model="systemTable" />
```

The `@if (... Count == 0)` wrappers around each table stay — they short-circuit to the green "No problems found" message, which is better UX than an empty table.

If the row types (`SingleUserIssueRow`, `LegacyEmailRow`, `SystemLevelIssueRow`) aren't resolved, they're defined in/near `src/Humans.Web/Models/EmailProblems/EmailProblemsListViewModel.cs` and `Humans.Application.DTOs.EmailProblems` — both namespaces are already imported by the view.

If the Razor compiler rejects the inline template syntax as a method argument or assignment (`@<text>` is the templated-Razor-delegate form; the `item` parameter is typed by the target `Func<T, IHtmlContent>`), the fallback is `Func<T, object>`-typed delegates and a `Cell` that accepts `object` results wrapped via `new HtmlString(...)` — but try the typed form first; it is expected to compile.

- [ ] **Step 2: Build**

Run: `dotnet build src/Humans.Web -v quiet`
Expected: Build succeeded.

- [ ] **Step 3: Verify by running the app (optional but recommended for the first conversion)**

Run: `dotnet run --project src/Humans.Web` and load `/ProfileAdmin/EmailProblems` (admin login required; dev login is enabled locally). Confirm: three tables render with consistent styling, header sort works (click), buttons/forms still work.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/ProfileAdmin/EmailProblems.cshtml
git commit -m "refactor(profiles): EmailProblems tables via TableModel component"
```

---

### Task 9: Flagship conversion 2 — `StoreAdmin/Summary` (client mode, sort + filter + footer)

**Files:**
- Modify: `src/Humans.Web/Views/StoreAdmin/Summary.cshtml`

Converts the by-counterparty table (replacing the hand-rolled `data-*` sort attributes AND the bespoke paid-filter `<script>` block) and the by-item table. **The cross-tab table stays raw** — it's a dynamic-column matrix, closer to a layout table; out of component scope per the spec.

**Visible change to flag in the PR description:** the hidden paid/partial/unpaid filter (card-header dropdown + inline script) becomes a visible "Payment" column with a standard column filter. Same capability, now visible data, and the CSP-nonce script block disappears.

- [ ] **Step 1: Replace the by-counterparty card body**

Add `@using Humans.Web.Models.Tables` and `@using Microsoft.AspNetCore.Html` at the top. Remove the paid-filter `<select id="paid-filter">` block from the card header (keep the `<h2>`). Replace everything inside `<div class="card-body p-0">` of the "By counterparty" card (the whole `@if/else` with the table, lines ~33-95) with:

```cshtml
        @if (s.ByCounterparty.Count == 0)
        {
            <p class="text-muted m-3 mb-3">No orders for @s.Year.</p>
        }
        else
        {
            @{
                Func<OrderSummaryDto, IHtmlContent> counterpartyCell =
                    @<text><a asp-controller="Store" asp-action="Order" asp-route-id="@item.OrderId">@item.CounterpartyName</a></text>;

                bool IsTeam(OrderSummaryDto r) =>
                    r.CounterpartyType == Humans.Domain.Enums.StoreOrderCounterpartyType.Team;

                string PaymentStatus(OrderSummaryDto r) =>
                    IsTeam(r) ? "paid"
                    : r.BalanceEur <= 0m ? "paid"
                    : r.PaymentsTotalEur > 0m ? "partial" : "unpaid";

                var counterpartyRows = s.ByCounterparty
                    .OrderBy(r => r.CounterpartyType)
                    .ThenBy(r => r.CounterpartyName, StringComparer.Ordinal)
                    .ToList();
                var camps = s.ByCounterparty
                    .Where(r => !IsTeam(r))
                    .ToList();

                var counterpartyTable = TableModel.For(counterpartyRows)
                    .Column("Type", r => r.CounterpartyType)
                    .Template("Counterparty", counterpartyCell)
                    .Column("State", r => r.State)
                    .Column("Payment", r => PaymentStatus(r), c => c.SelectFilter())
                    .Column("Total due (€)", r => r.TotalDueEur, c => c.Currency().End())
                    .Column("Paid (€)", r => IsTeam(r) ? null : r.PaymentsTotalEur, c => c.Currency().End())
                    .Column("Balance (€)", r => IsTeam(r) ? null : r.BalanceEur, c => c.Currency().End())
                    .Id("by-camp-table")
                    // Totals cover camps only: teams never pay (paid/balance hardcoded to 0),
                    // so including their due would make Total due ≠ Paid + Balance.
                    .Footer(new TableFooterCell("Total"),
                            new TableFooterCell($"{camps.Count} camps"),
                            new TableFooterCell(""),
                            new TableFooterCell(""),
                            new TableFooterCell(camps.Sum(r => r.TotalDueEur).ToString("N2"), "text-end"),
                            new TableFooterCell(camps.Sum(r => r.PaymentsTotalEur).ToString("N2"), "text-end"),
                            new TableFooterCell(camps.Sum(r => r.BalanceEur).ToString("N2"), "text-end"))
                    .Build();
            }
            <partial name="_Table" model="counterpartyTable" />
        }
```

The row type is `OrderSummaryDto` (`src/Humans.Application/Services/Store/Dtos/OrderSummaryDto.cs`) — `s.ByCounterparty` is `IReadOnlyList<OrderSummaryDto>`. Add `@using Humans.Application.Services.Store.Dtos` at the top of the view with the other usings.

- [ ] **Step 2: Replace the by-item table** (inside the "By item" card body, keep the empty-state `<p>`):

```cshtml
            @{
                var itemTable = TableModel.For(s.ByItem)
                    .Column("Product", r => r.ProductName)
                    .Column("Total qty", r => r.TotalQty, c => c.Number().End())
                    .Column("Total revenue (€)", r => r.TotalRevenueEur, c => c.Currency().End())
                    .Build();
            }
            <partial name="_Table" model="itemTable" />
```

- [ ] **Step 3: Delete the entire `@section Scripts { ... }` block** at the bottom of the view (the paid-filter script — its job is now the Payment column's select filter).

- [ ] **Step 4: Build, run, verify**

Run: `dotnet build src/Humans.Web -v quiet` → Build succeeded.
Then run the app and load `/StoreAdmin/Summary`: sorting works on every column (numbers sort numerically via `data-sort-value`), the Payment select filter shows paid/partial/unpaid, footer totals match the old view, cross-tab unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/StoreAdmin/Summary.cshtml
git commit -m "refactor(store): StoreAdmin/Summary tables via TableModel; paid-filter becomes a visible Payment column"
```

---

### Task 10: Flagship conversion 3 — `Ticket/Attendees` (server mode, controller untouched)

**Files:**
- Modify: `src/Humans.Web/Views/Ticket/Attendees.cshtml`

Replaces the GET filter form (lines ~10-53), the table with 8-route-param sort-link headers (lines ~57-129), and the trailing pager block (lines ~131-132). Keep: `<h1>`, `<partial name="_TicketNav" />`, and the `@Model.TotalCount attendees found` paragraph. **`TicketController.Attendees` is not modified at all.**

Known cosmetic deltas to flag in the PR: status filter option shows "CheckedIn" (value text) instead of "Checked In"; price column's first-click sort is ascending (was descending); select filters auto-submit.

- [ ] **Step 1: Rewrite the view**

```cshtml
@using Humans.Web.Models.Tables
@using Microsoft.AspNetCore.Html
@model Humans.Web.Models.TicketAttendeesViewModel
@{
    ViewData["Title"] = "Ticket Attendees";
}

<div class="container-fluid px-4">
    <h1 class="mt-4">Ticket Attendees</h1>
    <partial name="_TicketNav" />

    @{
        Func<TicketAttendeeRow, IHtmlContent> priceCell = @<text>
            @item.Price.ToString("N2")
            @if (item.IsVip)
            {
                <span class="badge bg-warning text-dark" title="VIP ticket">VIP</span>
            }
        </text>;
        Func<TicketAttendeeRow, IHtmlContent> vipSplitCell = @<text>
            @if (item.IsVip)
            {
                <small>
                    <span title="Taxable portion">@item.TaxableAmount.ToString("N2")</span>
                    + <span class="text-muted" title="VIP donation (VAT-free)">@item.VipDonation.ToString("N2") donation</span>
                </small>
            }
            else
            {
                <span class="text-muted">&mdash;</span>
            }
        </text>;
        Func<TicketAttendeeRow, IHtmlContent> matchedCell = @<text>
            @if (item.MatchedUserId.HasValue)
            {
                <vc:human user-id="@item.MatchedUserId.Value" link="Admin" />
            }
            else
            {
                <span class="text-muted">&mdash;</span>
            }
        </text>;
        Func<TicketAttendeeRow, IHtmlContent> orderCell = @<text>
            <small><a asp-action="Orders" asp-route-search="@item.VendorOrderId"><code>@item.VendorOrderId</code></a></small>
        </text>;
        Func<object?, IHtmlContent> extraFilters = @<text>
            <div class="col-md-2">
                <select name="filterMatched" class="form-select js-auto-submit" aria-label="Filter by matched">
                    <option value="">All</option>
                    <option value="true" selected="@(Model.FilterMatched == true ? "selected" : null)">Matched</option>
                    <option value="false" selected="@(Model.FilterMatched == false ? "selected" : null)">Unmatched</option>
                </select>
            </div>
            <div class="col-md-auto">
                <div class="form-check mt-2">
                    <input class="form-check-input" type="checkbox" name="filterMultipleTickets" value="true"
                           id="filterMultipleTickets" checked="@(Model.FilterMultipleTickets ? "checked" : null)" />
                    <label class="form-check-label" for="filterMultipleTickets"
                           title="Buyers (matched human or unmatched email) with more than one ticket">
                        Buyers with &gt;1 ticket
                    </label>
                </div>
            </div>
        </text>;

        var table = TableModel.For(Model.Attendees)
            .Column("Name", a => a.AttendeeName, c => c.Sort("name"))
            .Column("Email", a => a.AttendeeEmail, c => c.Sort("email").Css("small"))
            .Column("Ticket Type", a => a.TicketTypeName,
                c => c.Sort("type").SelectFilter(Model.AvailableTicketTypes, "filterTicketType", Model.FilterTicketType))
            .Template("Price", priceCell, c => c.Sort("price"))
            .Template("VIP Split", vipSplitCell)
            .Column("Status", a => a.Status,
                c => c.EnumBadge().Sort("status").SelectFilter(["Valid", "Void", "CheckedIn"], "filterStatus", Model.FilterStatus))
            .Template("Matched Human", matchedCell)
            .Template("Order", orderCell)
            .SearchBox("search", Model.Search, "Search name, email, or barcode...")
            .ServerMode(Model.SortBy, Model.SortDesc, new PagerViewModel(Model.TotalPages, Model.Page, "Attendees"))
            .HiddenField("pageSize", Model.PageSize.ToString())
            .HiddenField("filterOrderId", Model.FilterOrderId)
            .ExtraFilters(extraFilters)
            .Empty("No attendees found")
            .Build();
    }

    <p class="text-muted">@Model.TotalCount attendees found</p>

    <partial name="_Table" model="table" />
</div>
```

(The old view's `@using`/`@model` header lines: keep whatever namespace imports already exist — `TicketAttendeeRow` and `PagerViewModel` are in `Humans.Web.Models`, imported globally via `_ViewImports.cshtml`.)

- [ ] **Step 2: Build, run, verify against the old behavior**

Run: `dotnet build src/Humans.Web -v quiet` → Build succeeded.
Run the app, load `/Ticket/Attendees` (admin), verify: search + each filter round-trips and preserves the rest of the query; clicking Name/Email/Type/Price/Status headers toggles sort and preserves filters; pager works and preserves filters+sort; VIP badges, status badges, matched-human popovers, and order links render as before; sorting resets to page 1.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Ticket/Attendees.cshtml
git commit -m "refactor(tickets): Attendees via TableModel server mode — controller untouched"
```

---

### Task 11: Full verification + push

- [ ] **Step 1: Full build and test**

Run: `dotnet build Humans.slnx -v quiet` → Build succeeded, 0 errors.
Run: `dotnet test Humans.slnx -v quiet` → all tests pass.

- [ ] **Step 2: Verify main checkout untouched**

Run: `git -C H:/source/Humans status --short` → empty output (the worktree is the only place with changes).

- [ ] **Step 3: Push**

```bash
git push
```

- [ ] **Step 4: Smoke-test the three converted pages + WidgetGallery** (via local run or the PR preview deploy once a PR exists): `/WidgetGallery` (sort/filter/search the demo table), `/ProfileAdmin/EmailProblems`, `/StoreAdmin/Summary`, `/Ticket/Attendees`.

**Done means:** all 4 pages render and behave correctly, build + tests green, branch pushed. PR description must flag the two cosmetic deltas (Task 9 Payment column, Task 10 status-filter label / price first-click direction) for Peter's eyeball pass.
