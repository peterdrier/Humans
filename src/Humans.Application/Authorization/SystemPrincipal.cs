using System.Security.Claims;

namespace Humans.Application.Authorization;

/// <summary>
/// Provides a system-level ClaimsPrincipal for background jobs and automated processes
/// that need to call service methods requiring authorization context.
/// The system principal has a special "System" claim that authorization handlers
/// can check to bypass role-based checks.
/// </summary>
public static class SystemPrincipal
{
    /// <summary>
    /// Claim type used to identify the system principal.
    /// </summary>
    public const string SystemClaimType = "Humans.System";

    /// <summary>
    /// A singleton system principal for use by background jobs and automated processes.
    /// </summary>
    public static ClaimsPrincipal Instance { get; } = CreateSystemPrincipal();

    /// <summary>
    /// Checks whether the given principal is the system principal.
    /// </summary>
    public static bool IsSystem(ClaimsPrincipal principal)
    {
        return principal.HasClaim(c =>
            string.Equals(c.Type, SystemClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Value, "true", StringComparison.Ordinal));
    }

    private static ClaimsPrincipal CreateSystemPrincipal()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "System"),
            new Claim(SystemClaimType, "true")
        };

        var identity = new ClaimsIdentity(claims, "SystemAuth");
        return new ClaimsPrincipal(identity);
    }
}
