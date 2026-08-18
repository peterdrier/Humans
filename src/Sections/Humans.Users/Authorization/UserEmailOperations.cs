namespace Humans.Users.Authorization;

/// <summary>
/// Canonical user-email authorization operations. Use these singletons with
/// <c>IAuthorizationService.AuthorizeAsync(User, targetUserId, UserEmailOperations.Edit)</c>.
/// </summary>
internal static class UserEmailOperations
{
    public static readonly UserEmailOperationRequirement Edit = new(nameof(Edit));
}
