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
        string.Equals(d.Id, DateTimeFormatStringAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_DateTime_ToString_custom_format()
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
    public async Task Fires_on_interpolation_custom_format()
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
    public async Task Fires_on_NodaTime_Pattern_Create()
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
    public async Task Does_not_fire_on_NodaTime_standard_Iso_pattern()
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
    public async Task Does_not_fire_on_single_char_standard_format()
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
    public async Task Does_not_fire_on_non_date_ToString()
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
    public async Task Does_not_fire_inside_the_home_type()
    {
        var source = """
            using System;
            namespace Humans.Application.Extensions
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
    public async Task Does_not_fire_in_migration_namespace()
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

    [HumansFact]
    public async Task Does_not_fire_outside_production_assemblies()
    {
        var source = """
            using System;
            namespace Whatever
            {
                public class C { public string F(DateTime d) => d.ToString("d MMMM yyyy"); }
            }
            """;
        var diagnostics = await AnalyzerTestHarness.RunAsync(new DateTimeFormatStringAnalyzer(), "SomeTestAssembly", source);
        diagnostics.Should().BeEmpty();
    }
}
