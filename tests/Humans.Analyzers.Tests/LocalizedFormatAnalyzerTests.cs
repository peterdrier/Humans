using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class LocalizedFormatAnalyzerTests
{
    private const string Source = """
        namespace Microsoft.Extensions.Localization
        {
            public class LocalizedString
            {
                public string Value => "";
                public static implicit operator string(LocalizedString s) => s.Value;
            }

            public interface IStringLocalizer
            {
                LocalizedString this[string name] { get; }
                LocalizedString this[string name, params object[] arguments] { get; }
            }
        }

        namespace Humans.Finance
        {
            using System.Globalization;
            using Microsoft.Extensions.Localization;

            internal sealed class Emails(IStringLocalizer localizer)
            {
                public string ByValue(int n) => string.Format(CultureInfo.CurrentCulture, localizer["K"].Value, n);
                public string ByConversion(int n) => string.Format(localizer["K"], n);
                public string Indexer(int n) => localizer["K", n];
                public string Literal(int n) => string.Format(CultureInfo.InvariantCulture, "{0}", n);
            }
        }
        """;

    [HumansFact]
    public async Task Analyze_LocalizedFormat_ReportsOnlyHandFormatting()
    {
        var diagnostics = (await AnalyzerTestHarness.RunAsync(new LocalizedFormatAnalyzer(), "Humans.Finance", Source))
            .Where(d => string.Equals(d.Id, LocalizedFormatAnalyzer.DiagnosticId, StringComparison.Ordinal))
            .ToList();

        diagnostics.Should().HaveCount(2);
    }
}
