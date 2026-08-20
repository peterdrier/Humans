using System.Globalization;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Models.Tables;
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
        public EmailOutboxStatus Status { get; init; }
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

        Render(col.Cell(row)).Should().Be(1234.5m.ToString("N2", CultureInfo.CurrentCulture));
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
        col.SortValue(row).Should().Be(instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Render(col.Cell(new Row { When = null })).Should().Contain("—");
    }

    [HumansFact]
    public void EnumBadge_wraps_value_in_registered_badge()
    {
        var col = Col(CellFormat.EnumBadge, r => r.Status);
        // A Base enum, not a section's: a moved section's badge rows are registered from
        // its Section.Register and are absent in a unit test (see EnumBadgeMapTests). This
        // was SignupStatus until G5 lane 4b-i (nobodies-collective/Humans#866) moved Shifts'
        // rows out of Base's literal — the comment was already true, the enum was not.
        Render(col.Cell(new Row { Status = EmailOutboxStatus.Sent }))
            .Should().Be("""<span class="badge bg-success">Sent</span>""");
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
