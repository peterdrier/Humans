using AwesomeAssertions;
using Xunit;

namespace Humans.Analyzers.Tests;

public class NameLengthAnalyzerTests
{
    private const string Stubs = """
        namespace Xunit
        {
            public class FactAttribute : System.Attribute { }
            public class TheoryAttribute : FactAttribute { }
        }
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
        }
        """;

    private static async Task<string[]> RunAsync(string members, string baseline = "")
    {
        var source = Stubs + "namespace Demo { " + members + " }";
        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NameLengthAnalyzer(), "Demo", source,
            additionalFile: (NameLengthAnalyzer.BaselineFileName, baseline));
        return diagnostics.Select(d => d.Id).ToArray();
    }

    private static string Method(string name, string attribute = "") =>
        $"public class Service {{ {attribute} public void {name}() {{ }} }}";

    [HumansTheory]
    [InlineData("RemoveAsync", false)]
    [InlineData("GetOrCreateUserByEmailAsync", false)]
    [InlineData("GetActiveManagementRoleHolderIdsAsync", false)]
    [InlineData("GetActiveManagementRoleHolderUserIdsAsync", true)]
    [InlineData("AddMemberAndAssignRole", true)]
    [InlineData("SendToAndroid", false)]
    public async Task MethodName_OverSixWordsOrWithAnd_IsReported(string name, bool reported)
    {
        (await RunAsync(Method(name))).Should().Equal(reported ? [NameLengthAnalyzer.MethodNameId] : []);
    }

    [HumansTheory]
    [InlineData("Reassign_SurvivorHasPin_KeepsOwnPin", false)]
    [InlineData("VerifyAndConsume_ExpiredToken_Fails", false)]
    [InlineData("Reassign_MovesScans_AndCarriesPin", true)]
    [InlineData("A_merge_moves_the_pin", true)]
    [InlineData("Reassign_Survivor_HasPin_KeepsIt", true)]
    [InlineData("Reassign_SurvivorAlreadyHasTheirOwnPin_KeepsThatPinUnchanged", true)]
    public async Task TestName_OutsideTheShape_IsReported(string name, bool reported)
    {
        (await RunAsync(Method(name, "[Xunit.Theory]"))).Should().Equal(reported ? [NameLengthAnalyzer.TestNameId] : []);
    }

    [HumansFact]
    public async Task MethodName_ChosenElsewhere_IsExempt()
    {
        const string members = """
            public interface IService { void AddMemberAndAssignRole(); }
            public class Service : IService { public void AddMemberAndAssignRole() { } }
            public abstract class Base { public abstract void SaveAndClose(); }
            public class Derived : Base { public override void SaveAndClose() { } }
            public class HomeController : Microsoft.AspNetCore.Mvc.ControllerBase { public void SaveAndClose() { } }
            """;

        // The interface and the abstract declaration are reported once each; nothing that repeats them is.
        (await RunAsync(members)).Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Baseline_ListedName_IsSilent()
    {
        (await RunAsync(Method("AddMemberAndAssignRole"), "Demo.Service.AddMemberAndAssignRole\n")).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Baseline_StaleLine_IsReported()
    {
        (await RunAsync(Method("AddMember"), "Demo.Service.AddMember\nDemo.Service.Gone\n"))
            .Should().Equal(NameLengthAnalyzer.StaleBaselineId, NameLengthAnalyzer.StaleBaselineId);
    }
}
