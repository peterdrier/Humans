namespace Humans.Users.Authorization;

internal static class UserEmailOperations
{
    /// <summary>Board may view another member's emails; only Users Admin and Admin change them.</summary>
    public static readonly UserEmailOperationRequirement View = new(nameof(View));

    public static readonly UserEmailOperationRequirement Edit = new(nameof(Edit));
}
