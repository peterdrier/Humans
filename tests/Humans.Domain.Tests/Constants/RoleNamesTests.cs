using System.Reflection;
using AwesomeAssertions;
using Humans.Domain.Constants;

namespace Humans.Domain.Tests.Constants;

public class RoleNamesTests
{
    private static IEnumerable<string> DefinedRoleConstants() =>
        typeof(RoleNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [HumansFact]
    public void All_ContainsEveryDefinedRoleConstant()
    {
        var defined = DefinedRoleConstants().ToList();

        // The role-assignment filter bar enumerates RoleNames.All. Every role
        // constant must appear there (and nothing else) so a newly added role
        // surfaces in the UI automatically instead of being silently dropped.
        RoleNames.All.Should().BeEquivalentTo(defined);
    }

    [HumansFact]
    public void All_HasNoDuplicates() =>
        RoleNames.All.Should().OnlyHaveUniqueItems();
}
