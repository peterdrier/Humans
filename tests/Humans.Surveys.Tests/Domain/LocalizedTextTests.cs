using AwesomeAssertions;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Tests.Domain;

public class LocalizedTextTests
{
    [HumansFact]
    public void Resolve_CulturePresent_UsesRequestedCulture()
    {
        var t = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Hello", ["es"] = "Hola" });
        t.Resolve("es", "en").Should().Be("Hola");
    }

    [HumansFact]
    public void Resolve_CultureMissing_UsesDefaultThenAny()
    {
        var t = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Hello" });
        t.Resolve("de", "en").Should().Be("Hello");           // default-culture fallback
        new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["it"] = "Ciao" })
            .Resolve("de", "en").Should().Be("Ciao");          // any-present fallback
    }

    [HumansFact]
    public void ResolveOptional_UnrelatedCulture_ReturnsNothing()
    {
        var t = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = string.Empty,
            ["es"] = "Hola",
        });

        t.ResolveOptional("fr", "en").Should().BeEmpty();
        t.ResolveOptional("es", "en").Should().Be("Hola");
    }

    [HumansFact]
    public void Empty_Resolved_IsEmptyString()
    {
        LocalizedText.Empty.Resolve("en", "en").Should().BeEmpty();
        new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "x" })
            .Should().Be(new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "x" }));
    }
}
