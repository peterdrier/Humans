using AwesomeAssertions;
using Humans.Application.Services.Finance;
using Humans.Domain.Enums;

namespace Humans.Application.Tests.Finance;

public class HoldedMatcherTests
{
    private static readonly Guid CatA = Guid.NewGuid();
    private static readonly Guid CatB = Guid.NewGuid();

    [HumansFact]
    public void NormalizeTag_strips_separators_and_lowercases()
    {
        HoldedMatcher.NormalizeTag("Admin-Staff").Should().Be("adminstaff");
        HoldedMatcher.NormalizeTag("Operations / Toilets").Should().Be("operationstoilets");
        HoldedMatcher.NormalizeTag("  Comms_2  ").Should().Be("comms2");
        HoldedMatcher.NormalizeTag(null).Should().Be("");
    }

    [HumansFact]
    public void Match_prefers_account_over_tag()
    {
        var map = new[]
        {
            new HoldedMatchEntry(CatA, "acc-1", 6290001, "comms"),
            new HoldedMatchEntry(CatB, "acc-2", 6290002, "staff"),
        };
        var r = HoldedMatcher.Match("acc-1", new[] { "staff" }, map);
        r.CategoryId.Should().Be(CatA);
        r.Source.Should().Be(HoldedMatchSource.Account);
    }

    [HumansFact]
    public void Match_falls_back_to_tag_when_account_unmapped()
    {
        var map = new[] { new HoldedMatchEntry(CatB, "acc-2", 6290002, "staff") };
        var r = HoldedMatcher.Match("acc-generic", new[] { "Staff" }, map);
        r.CategoryId.Should().Be(CatB);
        r.Source.Should().Be(HoldedMatchSource.Tag);
    }

    [HumansFact]
    public void Match_returns_none_when_nothing_resolves()
    {
        var map = new[] { new HoldedMatchEntry(CatB, "acc-2", 6290002, "staff") };
        var r = HoldedMatcher.Match("acc-generic", new[] { "unknown" }, map);
        r.CategoryId.Should().BeNull();
        r.Source.Should().Be(HoldedMatchSource.None);
    }
}
