using AwesomeAssertions;
using Humans.Finance.Services;
using Humans.Finance.Domain;
using Xunit;

namespace Humans.Finance.Tests;

public class HoldedMatcherTests
{
    private static readonly Guid CatA = Guid.NewGuid();
    private static readonly Guid CatB = Guid.NewGuid();

    [HumansFact]
    public void NormalizeTag_Tag_LowercasedWithoutSeparators()
    {
        HoldedMatcher.NormalizeTag("Admin-Staff").Should().Be("adminstaff");
        HoldedMatcher.NormalizeTag("Operations / Toilets").Should().Be("operationstoilets");
        HoldedMatcher.NormalizeTag("  Comms_2  ").Should().Be("comms2");
        HoldedMatcher.NormalizeTag(null).Should().Be("");
    }

    [HumansFact]
    public void Match_AccountPlusTag_PrefersAccount()
    {
        var map = new[]
        {
            new HoldedMatchEntry(CatA, "acc-1", "comms"),
            new HoldedMatchEntry(CatB, "acc-2", "staff"),
        };
        var r = HoldedMatcher.Match("acc-1", new[] { "staff" }, map, new HashSet<string>(StringComparer.Ordinal));
        r.CategoryId.Should().Be(CatA);
        r.Source.Should().Be(HoldedMatchSource.Account);
    }

    [HumansFact]
    public void Match_UnmappedAccount_UsesTag()
    {
        var map = new[] { new HoldedMatchEntry(CatB, "acc-2", "staff") };
        var r = HoldedMatcher.Match("acc-generic", new[] { "Staff" }, map, new HashSet<string>(StringComparer.Ordinal));
        r.CategoryId.Should().Be(CatB);
        r.Source.Should().Be(HoldedMatchSource.Tag);
    }

    [HumansFact]
    public void Match_NothingResolves_ReturnsNone()
    {
        var map = new[] { new HoldedMatchEntry(CatB, "acc-2", "staff") };
        var r = HoldedMatcher.Match("acc-generic", new[] { "unknown" }, map, new HashSet<string>(StringComparer.Ordinal));
        r.CategoryId.Should().BeNull();
        r.Source.Should().Be(HoldedMatchSource.None);
    }

    [HumansTheory]
    [InlineData("Workgroups / ALM 2027", "workgroups / alm 2027")]
    [InlineData("  Workgroups  /  ALM   2027 ", "workgroups / alm 2027")]
    [InlineData("Workgroups / Asamblea Éxito", "workgroups / asamblea exito")]
    [InlineData(null, "")]
    public void NormalizeAccountName_Name_LowercasedWithoutAccents(string? raw, string expected)
    {
        HoldedMatcher.NormalizeAccountName(raw).Should().Be(expected);
    }

    [HumansFact]
    public void Match_ManagedAccount_MatchesWithoutCategory()
    {
        var map = new[] { new HoldedMatchEntry(Guid.NewGuid(), "cat-0", "geeks") };
        var managed = new HashSet<string>(StringComparer.Ordinal) { "m-1" };

        var result = HoldedMatcher.Match("m-1", ["geeks"], map, managed);

        result.CategoryId.Should().BeNull();
        result.Source.Should().Be(HoldedMatchSource.Account);
        result.IsManaged.Should().BeTrue();
    }

    [HumansFact]
    public void Match_CategoryAccountWinsOverManagedSet()
    {
        var cat = Guid.NewGuid();
        var map = new[] { new HoldedMatchEntry(cat, "cat-0", "geeks") };
        var managed = new HashSet<string>(StringComparer.Ordinal) { "cat-0" };

        var result = HoldedMatcher.Match("cat-0", [], map, managed);

        result.CategoryId.Should().Be(cat);
        result.IsManaged.Should().BeFalse();
    }
}
