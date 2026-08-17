using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Xunit;

namespace Humans.GoogleIntegration.Tests.Enums;

/// <summary>
/// GoogleIntegration's share of the string-stored-enum guard, moved out of
/// <c>Humans.Domain.Tests.Enums.EnumStringStabilityTests</c> when that orphaned project's rows
/// were distributed to their owners —
/// <see cref="GoogleResourceType"/>, <see cref="SyncMode"/>, <see cref="SyncServiceType"/>,
/// <see cref="SyncAction"/> and <see cref="DrivePermissionLevel"/> live on
/// <c>Humans.GoogleIntegration.Contracts</c>, so the guard belongs to the section that owns them
/// (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// All five are persisted with <c>HasConversion&lt;string&gt;()</c>: renaming a member leaves
/// the OLD string in the column. A rename needs a migration that UPDATEs the stored values.
/// </remarks>
public class EnumStringStabilityTests
{
    [HumansTheory]
    [MemberData(nameof(StringStoredEnumData))]
    public void StringStoredEnum_MemberNames_MustMatchExpected(
        Type enumType, string[] expectedNames)
    {
        var actualNames = Enum.GetNames(enumType);

        foreach (var expected in expectedNames)
        {
            actualNames.Should().Contain(expected,
                $"enum {enumType.Name} member '{expected}' is stored as a string in the DB. " +
                $"If you renamed it, create a DB migration to UPDATE the old values.");
        }
    }

    public static TheoryData<Type, string[]> StringStoredEnumData => new()
    {
        {
            typeof(GoogleResourceType), ["DriveFolder", "SharedDrive", "Group", "DriveFile"]
        },
        {
            typeof(SyncMode), ["None", "AddOnly", "AddAndRemove"]
        },
        {
            typeof(SyncServiceType), ["GoogleDrive", "GoogleGroups", "Discord"]
        },
        {
            typeof(SyncAction), ["Preview", "Execute"]
        },
        {
            typeof(DrivePermissionLevel), ["None", "Viewer", "Commenter", "Contributor", "ContentManager", "Manager"]
        }
    };
}
