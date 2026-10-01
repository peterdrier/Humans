using System.Security.Cryptography;

namespace Humans.Base.Helpers;

public static class PasswordGenerator
{
    private const string Characters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$";

    /// <summary>
    /// Generates a 16-character temporary password for Google Workspace provisioning.
    /// </summary>
    public static string GenerateTemporary()
    {
        return new string(Enumerable.Range(0, 16)
            .Select(_ => Characters[RandomNumberGenerator.GetInt32(Characters.Length)])
            .ToArray());
    }
}
