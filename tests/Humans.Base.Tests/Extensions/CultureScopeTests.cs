using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Xunit;

namespace Humans.Base.Tests.Extensions;

public class CultureScopeTests
{
    [HumansFact]
    public void Sets_both_cultures_inside_the_scope_and_restores_them_after()
    {
        var before = CultureInfo.CurrentCulture;
        var beforeUi = CultureInfo.CurrentUICulture;

        using (new CultureScope("de"))
        {
            CultureInfo.CurrentCulture.Name.Should().Be("de");
            CultureInfo.CurrentUICulture.Name.Should().Be("de");
        }

        CultureInfo.CurrentCulture.Should().BeSameAs(before);
        CultureInfo.CurrentUICulture.Should().BeSameAs(beforeUi);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!invalid!!")]
    public void Leaves_the_ambient_culture_alone_for_a_blank_or_unknown_culture(string? culture)
    {
        var before = CultureInfo.CurrentCulture;

        using (new CultureScope(culture))
        {
            CultureInfo.CurrentCulture.Should().BeSameAs(before);
        }

        CultureInfo.CurrentCulture.Should().BeSameAs(before);
    }
}
