using System.Security.Claims;

namespace Humans.Base.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id; null when anonymous or the name-identifier claim is not a user id.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
