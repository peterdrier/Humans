using AwesomeAssertions;
using Humans.Base.Helpers;

namespace Humans.Base.Tests.Helpers;

public sealed class PasswordGeneratorTests
{
    private const string AllowedCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$";

    [HumansFact]
    public void GenerateTemporary_UsesTheReadablePasswordAlphabet()
    {
        var password = PasswordGenerator.GenerateTemporary();

        password.Should().HaveLength(16);
        password.All(AllowedCharacters.Contains).Should().BeTrue();
    }
}
