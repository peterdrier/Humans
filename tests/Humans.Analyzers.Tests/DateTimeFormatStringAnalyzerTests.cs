using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public sealed class DateTimeFormatStringAnalyzerTests
{
    private const string NodaStub = """
        namespace NodaTime
        {
            public struct LocalDate { public override string ToString() => ""; public string ToString(string p, System.IFormatProvider f) => ""; }
        }
        namespace NodaTime.Text
        {
            public sealed class LocalDatePattern
            {
                public static LocalDatePattern Iso => new();
                public static LocalDatePattern Create(string patternText, System.Globalization.CultureInfo culture) => new();
            }
        }
        """;

    private static bool IsHum0030(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, DateTimeFormatStringAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Analyze_DateTimeCustomFormat_Fires()
    {
        var source = """
            using System;
            namespace Humans.Web.Models
            {
                public class Vm { public string F(DateTime d) => d.ToString("d MMMM yyyy"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Web", source);
        diagnostics.Should().ContainSingle(d => IsHum0030(d));
    }

    [HumansFact]
    public async Task Analyze_InterpolationCustomFormat_Fires()
    {
        var source = """
            using System;
            namespace Humans.Application.Services
            {
                public class S { public string F(DateTime d) => $"{d:MMM d}"; }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Application", source);
        diagnostics.Should().ContainSingle(d => IsHum0030(d));
    }

    [HumansFact]
    public async Task Analyze_NodaTimePatternCreate_Fires()
    {
        var source = NodaStub + """

            namespace Humans.Infrastructure.Services
            {
                public class S { public object F() => NodaTime.Text.LocalDatePattern.Create("uuuu-MM-dd", null); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Infrastructure", source);
        diagnostics.Should().ContainSingle(d => IsHum0030(d));
    }

    [HumansFact]
    public async Task Analyze_NodaTimeIsoPattern_IsSilent()
    {
        var source = NodaStub + """

            namespace Humans.Infrastructure.Services
            {
                public class S { public object F() => NodaTime.Text.LocalDatePattern.Iso; }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Infrastructure", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_SingleCharStandardFormat_IsSilent()
    {
        var source = """
            using System;
            namespace Humans.Web.Models
            {
                public class Vm { public string F(DateTime d) => d.ToString("g") + d.ToString("o") + d.ToString("d"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Web", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_NonDateToString_IsSilent()
    {
        var source = """
            namespace Humans.Web.Models
            {
                public class Vm { public string F(decimal x, System.Guid g) => x.ToString("N2") + g.ToString("D"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Web", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_HomeType_IsSilent()
    {
        var source = """
            using System;
            namespace Humans.Base.Extensions
            {
                public static class DateFormattingExtensions
                {
                    public static string ToDisplayLongDate(this DateTime v) => v.ToString("d MMMM yyyy");
                }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Application", source);
        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_MigrationNamespace_IsSilent()
    {
        var source = """
            using System;
            namespace Humans.Infrastructure.Migrations
            {
                public class M { public string F(DateTime d) => d.ToString("yyyy-MM-dd"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "Humans.Infrastructure", source);
        diagnostics.Should().BeEmpty();
    }

}
