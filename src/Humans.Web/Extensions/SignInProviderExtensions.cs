using Humans.Base.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Humans.Web.Extensions;

/// <summary>
/// External sign-in providers. Each is registered only when its client id and secret are set, so an instance turns a
/// provider on by setting them and off by leaving them empty. The login page and the profile's link-account cards
/// list whatever is registered. Adding a provider: "Adding a sign-in provider" in the Auth section doc.
/// </summary>
public static class SignInProviderExtensions
{
    public static AuthenticationBuilder AddSignInProviders(
        this AuthenticationBuilder authentication, IConfiguration configuration, ConfigurationRegistry registry)
    {
        if (ReadCredentials(configuration, registry, "Google", ConfigurationImportance.Recommended) is { } google)
        {
            authentication.AddGoogle(options =>
            {
                options.ClientId = google.ClientId;
                options.ClientSecret = google.ClientSecret;
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.SaveTokens = false;
                // Account takeover by email needs the provider's email_verified claim.
                options.ClaimActions.MapJsonKey("email_verified", "email_verified", "boolean");
                options.Events = new OAuthEvents { OnRemoteFailure = ReturnToLoginOnFailureAsync };
            });
        }

        return authentication;
    }

    // Registering a scheme with an empty client id would fail every request, not just sign-in, so a provider
    // with either value missing is not registered at all.
    private static (string ClientId, string ClientSecret)? ReadCredentials(
        IConfiguration configuration, ConfigurationRegistry registry, string provider,
        ConfigurationImportance importance = ConfigurationImportance.Optional)
    {
        var clientId = configuration.GetOptionalSetting(
            registry, $"Authentication:{provider}:ClientId", "Authentication", isSensitive: true, importance);
        var clientSecret = configuration.GetOptionalSetting(
            registry, $"Authentication:{provider}:ClientSecret", "Authentication", isSensitive: true, importance);
        return string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)
            ? null
            : (clientId, clientSecret);
    }

    // Every provider's OnRemoteFailure: a failed or cancelled sign-in returns to the login page with a message
    // instead of a 500. Warning plus client IP so /Debug/Logs traces user-reported sign-in issues.
    private static Task ReturnToLoginOnFailureAsync(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SignInProviders");
        var provider = context.Scheme.Name;
        var failureMessage = context.Failure?.Message ?? string.Empty;
        var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        string error;
        if (failureMessage.Contains("access_denied", StringComparison.OrdinalIgnoreCase)
            || failureMessage.Contains("denied by the resource owner", StringComparison.OrdinalIgnoreCase))
        {
            error = "cancelled";
            logger.LogWarning("{Provider} sign-in cancelled by user (access_denied) from {ClientIp}", provider, clientIp);
        }
        else if (failureMessage.Contains("Correlation", StringComparison.OrdinalIgnoreCase))
        {
            error = "cookie-missing";
            logger.LogWarning("{Provider} sign-in correlation cookie missing from {ClientIp}", provider, clientIp);
        }
        else
        {
            error = "sign-in-failed";
            // OperationCanceledException: the user closed the tab or the network dropped mid-callback.
            if (context.Failure is not OperationCanceledException)
                logger.LogWarning(context.Failure, "{Provider} sign-in failed from {ClientIp}: {Error}",
                    provider, clientIp, failureMessage);
        }

        context.Response.Redirect($"/Account/Login?error={error}");
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
