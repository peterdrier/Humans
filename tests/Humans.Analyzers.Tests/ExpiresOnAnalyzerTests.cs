using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

/// <summary>
/// Tests pin "today" via <see cref="ExpiresOnAnalyzer.TodayOverride"/> so the
/// suite is deterministic across machines and dates. Each test restores the
/// override in a try/finally.
/// </summary>
public class ExpiresOnAnalyzerTests
{
    private const string AttributeStub = """
        namespace Humans.Domain.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class ExpiresOnAttribute : System.Attribute
            {
                public ExpiresOnAttribute(string date, int graceDays = 7, string? reason = null)
                {
                    Date = date;
                    GraceDays = graceDays;
                    Reason = reason;
                }
                public string Date { get; }
                public int GraceDays { get; }
                public string? Reason { get; }
            }
        }
        """;

    private const string SampleSource = AttributeStub + """

        namespace Sample
        {
            using Humans.Domain.Architecture;

            public class Container
            {
                [ExpiresOn("2026-05-26", 7, "use NewMethod instead")]
                public int Foo { get; set; }

                public void NewMethod() { }
            }

            public class Caller
            {
                public int Read(Container c) => c.Foo;
                public void Write(Container c) => c.Foo = 1;
            }
        }
        """;

    private static bool IsUsage(Diagnostic d) =>
        string.Equals(d.Id, ExpiresOnAnalyzer.UsageDiagnosticId, StringComparison.Ordinal);

    private static bool IsDeclaration(Diagnostic d) =>
        string.Equals(d.Id, ExpiresOnAnalyzer.DeclarationDiagnosticId, StringComparison.Ordinal);

    private static async Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> RunAtAsync(
        DateTime today,
        string source = SampleSource)
    {
        ExpiresOnAnalyzer.TodayOverride = () => today;
        try
        {
            return await AnalyzerTestHarness.RunAsync(
                new ExpiresOnAnalyzer(),
                "Humans.Application",
                source);
        }
        finally
        {
            ExpiresOnAnalyzer.TodayOverride = null;
        }
    }

    [HumansFact]
    public async Task Before_date_callers_warn_and_declaration_clean()
    {
        var diagnostics = await RunAtAsync(new DateTime(2026, 5, 12));

        var usage = diagnostics.Where(IsUsage).ToArray();
        usage.Should().HaveCount(2, "both Read and Write reference Foo");
        usage.Should().AllSatisfy(d => d.Severity.Should().Be(DiagnosticSeverity.Warning));

        diagnostics.Where(IsDeclaration).Should().BeEmpty();
    }

    [HumansFact]
    public async Task On_date_callers_error_and_declaration_warns_grace_period()
    {
        var diagnostics = await RunAtAsync(new DateTime(2026, 5, 26));

        diagnostics.Where(IsUsage).Should().AllSatisfy(d =>
            d.Severity.Should().Be(DiagnosticSeverity.Error));

        var decl = diagnostics.Where(IsDeclaration).ToArray();
        decl.Should().ContainSingle();
        decl[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Within_grace_window_declaration_still_warns()
    {
        var diagnostics = await RunAtAsync(new DateTime(2026, 6, 1));

        var decl = diagnostics.Where(IsDeclaration).ToArray();
        decl.Should().ContainSingle();
        decl[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task After_grace_window_declaration_errors()
    {
        var diagnostics = await RunAtAsync(new DateTime(2026, 6, 3));

        var decl = diagnostics.Where(IsDeclaration).ToArray();
        decl.Should().ContainSingle();
        decl[0].Severity.Should().Be(DiagnosticSeverity.Error);

        diagnostics.Where(IsUsage).Should().AllSatisfy(d =>
            d.Severity.Should().Be(DiagnosticSeverity.Error));
    }

    [HumansFact]
    public async Task Custom_grace_days_extends_warning_window()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Domain.Architecture;

                public class Container
                {
                    [ExpiresOn("2026-05-26", graceDays: 30)]
                    public void Old() { }
                }
            }
            """;

        var diagnostics = await RunAtAsync(new DateTime(2026, 6, 20), source);

        var decl = diagnostics.Where(IsDeclaration).ToArray();
        decl.Should().ContainSingle();
        decl[0].Severity.Should().Be(DiagnosticSeverity.Warning,
            "30-day grace covers 2026-05-26 through 2026-06-25");
    }

    [HumansFact]
    public async Task Class_level_attribute_flags_member_usage()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Domain.Architecture;

                [ExpiresOn("2026-05-26")]
                public class LegacyService
                {
                    public void DoThing() { }
                }

                public class Caller
                {
                    public void Use() => new LegacyService().DoThing();
                }
            }
            """;

        var diagnostics = await RunAtAsync(new DateTime(2026, 5, 12), source);

        var usage = diagnostics.Where(IsUsage).ToArray();
        usage.Should().NotBeEmpty(
            "constructing or calling a member of an [ExpiresOn] class is a usage");
        usage.Should().AllSatisfy(d => d.Severity.Should().Be(DiagnosticSeverity.Warning));
    }

    [HumansFact]
    public async Task No_attribute_no_diagnostics()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                public class Plain
                {
                    public int Foo { get; set; }
                }

                public class Caller
                {
                    public int Use(Plain p) => p.Foo;
                }
            }
            """;

        var diagnostics = await RunAtAsync(new DateTime(2026, 5, 26), source);

        diagnostics.Where(d => IsUsage(d) || IsDeclaration(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Malformed_member_attribute_falls_through_to_valid_class_attribute()
    {
        // A malformed [ExpiresOn] on a member must not suppress a valid one
        // on its containing type — the loop should `continue`, not `return`.
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Domain.Architecture;

                [ExpiresOn("2026-05-26")]
                public class LegacyService
                {
                    [ExpiresOn("not-a-date")]
                    public void DoThing() { }
                }

                public class Caller
                {
                    public void Use(LegacyService s) => s.DoThing();
                }
            }
            """;

        var diagnostics = await RunAtAsync(new DateTime(2026, 5, 12), source);

        var usage = diagnostics.Where(IsUsage).ToArray();
        usage.Should().NotBeEmpty(
            "the class-level [ExpiresOn] is valid; the malformed member-level " +
            "attribute must not block its discovery");
        usage.Should().AllSatisfy(d => d.Severity.Should().Be(DiagnosticSeverity.Warning));
    }

    [HumansFact]
    public async Task Malformed_date_emits_no_diagnostic()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Domain.Architecture;

                public class Container
                {
                    [ExpiresOn("not-a-date")]
                    public void Old() { }
                }

                public class Caller
                {
                    public void Use(Container c) => c.Old();
                }
            }
            """;

        var diagnostics = await RunAtAsync(new DateTime(2026, 5, 26), source);

        diagnostics.Where(d => IsUsage(d) || IsDeclaration(d)).Should().BeEmpty(
            "a malformed date is a no-op rather than a hard failure");
    }
}
