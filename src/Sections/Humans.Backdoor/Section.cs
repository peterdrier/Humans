using Humans.Backdoor.Authentication;
using Humans.Backdoor.Data;
using Humans.Backdoor.Filters;
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
using ModelContextProtocol.Server;
using Humans.AuditLog.Contracts;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Backdoor;

public sealed class Section : ISection, ISectionAdminNav, ISectionEndpoints
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, BackdoorAuditActions>();
        services.AddSectionDbContext<BackdoorDbContext>(sentinelTable: "backdoor_api_keys");

        services.AddSingleton<IBackdoorApiKeyRepository, BackdoorApiKeyRepository>();
        services.AddScoped<BackdoorApiKeyService>();
        services.AddScoped<IBackdoorApiKeyService>(sp => sp.GetRequiredService<BackdoorApiKeyService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BackdoorApiKeyService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<BackdoorApiKeyService>());

        // [ServiceFilter] resolves the filter from the container, so it must be registered.
        services.AddScoped<BackdoorApiKeyAuthFilter>();

        AddMcp(services);
    }

    void ISectionEndpoints.MapEndpoints(IEndpointRouteBuilder endpoints) => MapMcp(endpoints);

    /// <summary>The key scheme and the MCP server. Tests build their host from this, so they run what ships.</summary>
    internal static IMcpServerBuilder AddMcp(IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BackdoorApiKeyHandler>(BackdoorApiKeyHandler.SchemeName, null);
        services.Configure<McpServerOptions>(McpToolScopeGate.Install);

        // Stateless: nothing is kept between calls, so every call is signed in and scoped afresh.
        return services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<HelloTool>();
    }

    /// <summary>
    /// Only the Backdoor schemes, never the browser cookie, so a page open in a signed-in browser cannot drive it.
    /// </summary>
    internal static void MapMcp(IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp(McpPath).RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(BackdoorApiKeyHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireAssertion(async context =>
                context.Resource is HttpContext http
                && context.User.GetUserId() is { } userId
                && await http.RequestServices.GetRequiredService<IBackdoorApiKeyService>()
                    .IsEligibleAsync(userId, http.RequestAborted)));

    internal const string McpPath = "/api/backdoor/mcp";

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Backdoor", [
            new("API keys", "Backdoor", "Index", null, null, "fa-solid fa-key", PolicyNames.BoardOrAdmin)
        ])
    ];
}
