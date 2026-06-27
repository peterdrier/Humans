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
            .Template("Actions", _ => new HtmlString("<a>x</a>"))
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
    public void Sort_captures_descending_first_per_column()
    {
        var table = TableModel.For(Rows)
            .Column("Name", r => r.Name, c => c.Sort("name"))
            .Column("Amount", r => r.Amount, c => c.Sort("amount", descendingFirst: true))
            .Build();

        table.Columns[0].SortDescFirst.Should().BeFalse();
        table.Columns[1].SortDescFirst.Should().BeTrue();
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
