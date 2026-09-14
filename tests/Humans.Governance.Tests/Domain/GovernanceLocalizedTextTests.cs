using AwesomeAssertions;
using Humans.Governance.Domain;
using Xunit;

namespace Humans.Governance.Tests.Domain;

public class GovernanceLocalizedTextTests
{
    private static GovernanceLocalizedText Text(params (string Culture, string Value)[] values) =>
        new(values.ToDictionary(v => v.Culture, v => v.Value, StringComparer.OrdinalIgnoreCase));

    [HumansFact]
    public void Resolve_WhenTheRequestedCultureHoldsOnlyWhitespace_FallsBackToTheOfficialText()
    {
        // Optional translations are neither required nor trimmed on the draft form, so a
        // stray space is the realistic input. Treating it as authored puts a blank title on
        // a binding vote's ballot page and a blank subject on its email.
        var text = Text(("en", "Adopt the budget"), ("es", "   "));

        text.Resolve("es", "en").Should().Be("Adopt the budget");
    }

    [HumansFact]
    public void HasCulture_ForAWhitespaceOnlyTranslation_IsFalse()
    {
        // Must agree with Resolve: if it said true while Resolve fell back, the page would
        // show the official-culture wording without the "this is a translation" notice.
        var text = Text(("en", "Adopt the budget"), ("es", " "));

        text.HasCulture("es").Should().BeFalse();
        text.HasCulture("en").Should().BeTrue();
    }

    [HumansFact]
    public void Resolve_WhenEveryCultureIsBlank_IsEmpty()
    {
        Text(("en", " "), ("es", "\t")).Resolve("es", "en").Should().BeEmpty();
    }
}
