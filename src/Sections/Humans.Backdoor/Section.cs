using Humans.Backdoor.Authentication;
using Humans.Backdoor.Data;
using Humans.Backdoor.Domain;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Jobs;
using Humans.Backdoor.OAuth;
using Humans.Backdoor.Mcp;
using Humans.Backdoor.Services;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Server;
using Humans.Notifications.Contracts;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Humans.Audit.Contracts;
using Humans.Backdoor.Controllers;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Backdoor;

public sealed class Section : SectionBase, ISectionAdminNav, ISectionEndpoints, ISectionJobs
{
    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, BackdoorAuditActions>();
        services.AddSectionDbContext<BackdoorDbContext>(sentinelTable: "backdoor_api_keys");

        services.AddSingleton<IBackdoorApiKeyRepository, BackdoorApiKeyRepository>();
        services.AddScoped<BackdoorApiKeyService>();
        services.AddScoped<IBackdoorApiKeyService>(sp => sp.GetRequiredService<BackdoorApiKeyService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BackdoorApiKeyService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<BackdoorApiKeyService>());

        services.AddSingleton<IBackdoorOAuthRepository, BackdoorOAuthRepository>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BackdoorConnectionService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<BackdoorConnectionService>());
        services.AddSingleton<INotificationSourceContributor, BackdoorNotificationSources>();
        services.AddScoped<BackdoorOAuthPruneJob>();

        // [ServiceFilter] resolves the filter from the container, so it must be registered.
        services.AddScoped<BackdoorApiKeyAuthFilter>();

        AddMcp(services);
    }

    void ISectionEndpoints.MapEndpoints(IEndpointRouteBuilder endpoints) => MapMcp(endpoints);

    /// <summary>
    /// Both ways in (the key scheme and "Sign in with Humans") and the MCP server. Tests build their host from this, so
    /// they run what ships; only the repositories are left to <see cref="Register"/>.
    /// </summary>
    internal static IMcpServerBuilder AddMcp(IServiceCollection services)
    {
        services.AddScoped<BackdoorConnectionService>();
        services.AddScoped<IBackdoorConnectionService>(sp => sp.GetRequiredService<BackdoorConnectionService>());
        AddSignInWithHumans(services);

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BackdoorApiKeyHandler>(BackdoorApiKeyHandler.SchemeName, null)
            .AddPolicyScheme(McpCallerScheme, null, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(BackdoorApiKeyHandler.HeaderName)
                    ? BackdoorApiKeyHandler.SchemeName
                    : OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            // Answers a refused call with the pointer the Claude app follows to find the sign-in.
            .AddMcp(options =>
            {
                options.ForwardAuthenticate = McpCallerScheme;
                options.Events.OnResourceMetadataRequest = context =>
                {
                    context.ResourceMetadata = new ProtectedResourceMetadata
                    {
                        Resource = McpResource(context.HttpContext.Request),
                        AuthorizationServers = [BaseUrl(context.HttpContext.Request)],
                        ScopesSupported = [Scopes.OfflineAccess],
                    };
                    return Task.CompletedTask;
                };
            });
        services.Configure<McpServerOptions>(McpToolScopeGate.Install);
        services.AddHttpContextAccessor();

        // Stateless: nothing is kept between calls, so every call is signed in and scoped afresh.
        return services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<HelloTool>();
    }

    /// <summary>
    /// Humans as an OAuth server for the clients in <see cref="BackdoorOAuthClients"/>: authorization code with PKCE,
    /// rotating refresh tokens that do not slide, and tokens checked against their stored connection on every call.
    /// </summary>
    private static void AddSignInWithHumans(IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.SetDefaultApplicationEntity<BackdoorOAuthClient>()
                    .SetDefaultAuthorizationEntity<BackdoorOAuthConnection>()
                    .SetDefaultTokenEntity<BackdoorOAuthToken>()
                    .ReplaceApplicationStore<BackdoorOAuthClient, BackdoorOAuthClientStore>(ServiceLifetime.Singleton)
                    .ReplaceAuthorizationStore<BackdoorOAuthConnection, BackdoorOAuthConnectionStore>(ServiceLifetime.Scoped)
                    .ReplaceTokenStore<BackdoorOAuthToken, BackdoorOAuthTokenStore>(ServiceLifetime.Scoped);
            })
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris(AuthorizePath)
                    .SetTokenEndpointUris(TokenPath)
                    .AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange()
                    .SetAccessTokenLifetime(TimeSpan.FromHours(1))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(30))
                    .DisableSlidingRefreshTokenExpiration()
                    // The approval grants offline_access and this server's MCP address whatever was asked, so asked-for
                    // scopes and resources are not checked against a list.
                    .DisableScopeValidation()
                    .IgnoreScopePermissions()
                    .DisableResourceValidation()
                    .IgnoreResourcePermissions()
                    // Tokens are sealed with the app's Data Protection keys, kept in the database, so a deploy keeps
                    // everyone connected. The ephemeral keys only sign the unused key set document.
                    .AddEphemeralEncryptionKey()
                    .AddEphemeralSigningKey();
                options.UseDataProtection();
                options.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseDataProtection();
                options.UseAspNetCore();
                options.EnableAuthorizationEntryValidation();
                options.EnableTokenEntryValidation();
            });
    }

    internal const string AuthorizePath = "Backdoor/OAuth/Authorize";

    internal const string TokenPath = "api/backdoor/oauth/token";

    /// <summary>Picks the key scheme when a key is sent, else the sign-in token.</summary>
    internal const string McpCallerScheme = "BackdoorMcpCaller";

    /// <summary>This server's MCP address: what every token is made out to.</summary>
    internal static string McpResource(HttpRequest request) => BaseUrl(request) + McpPath;

    private static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>
    /// Only the Backdoor ways in (a key or a sign-in token), never the browser cookie, so a page open in a signed-in
    /// browser cannot drive it.
    /// </summary>
    internal static void MapMcp(IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp(McpPath).RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(async context =>
                context.Resource is HttpContext http
                && context.User.GetUserId() is { } userId
                && await http.RequestServices.GetRequiredService<IBackdoorApiKeyService>()
                    .IsEligibleAsync(userId, http.RequestAborted)));

    internal const string McpPath = "/api/backdoor/mcp";

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<BackdoorController>("API keys", nameof(BackdoorController.Index), "fa-solid fa-key", PolicyNames.BoardOrAdmin)
        ])
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor("backdoor-oauth-prune", typeof(BackdoorOAuthPruneJob), "40 3 * * *");
    }
}
