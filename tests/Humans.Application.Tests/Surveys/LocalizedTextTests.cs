using AwesomeAssertions;
using Humans.Domain.ValueObjects;
using Xunit;

namespace Humans.Application.Tests.Surveys;

public class LocalizedTextTests
{
    [HumansFact]
    public void Resolve_prefers_requested_culture()
    {
        var t = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Hello", ["es"] = "Hola" });
        t.Resolve("es", "en").Should().Be("Hola");
    }

    [HumansFact]
    public void Resolve_falls_back_to_default_then_any_present()
    {
        var t = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Hello" });
        t.Resolve("de", "en").Should().Be("Hello");           // default-culture fallback
        new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["it"] = "Ciao" })
            .Resolve("de", "en").Should().Be("Ciao");          // any-present fallback
    }

    [HumansFact]
    public void Empty_resolves_to_empty_string_and_equality_is_by_value()
    {
        LocalizedText.Empty.Resolve("en", "en").Should().BeEmpty();
        new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "x" })
            .Should().Be(new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "x" }));
    }
}
