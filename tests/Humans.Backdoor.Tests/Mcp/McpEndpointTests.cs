using System.ComponentModel;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Backdoor.Authentication;
using Humans.Backdoor.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using NSubstitute;

namespace Humans.Backdoor.Tests.Mcp;

/// <summary>
/// A real host with the shipped registration and mapping (<see cref="Section.AddMcp"/>, <see cref="Section.MapMcp"/>),
/// a signed-in browser session as the default scheme, and the SDK's own client.
/// </summary>
public sealed class McpEndpointTests : IAsyncDisposable
{
    private const string AdminKey = "admin-key";
    private const string TasksKey = "tasks-key";

    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _tasksManager = Guid.NewGuid();
    private readonly IBackdoorApiKeyService _keys = Substitute.For<IBackdoorApiKeyService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private WebApplication? _app;

    public McpEndpointTests()
    {
        _keys.ResolveOwnerAsync(AdminKey, Arg.Any<CancellationToken>()).Returns(new BackdoorKeyOwner(_admin, []));
        _keys.ResolveOwnerAsync(TasksKey, Arg.Any<CancellationToken>()).Returns(new BackdoorKeyOwner(_tasksManager, []));
        _keys.IsEligibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _keys.AdmitsAsync(_admin, BackdoorScope.AdminAndBoard, Arg.Any<CancellationToken>()).Returns(true);
        _keys.AdmitsAsync(_tasksManager, BackdoorScope.Tasks, Arg.Any<CancellationToken>()).Returns(true);
    }

    [HumansFact]
    public async Task Post_BrowserSessionWithoutKey_Returns401()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var baseUri = await StartAsync(ct);
        using var http = new HttpClient();
        using var body = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var response = await http.PostAsync(new Uri(baseUri, Section.McpPath), body, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [HumansFact]
    public async Task Hello_AdminKey_ReturnsOwnerSignedInWithKey()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _users.GetUserInfoAsync(_admin, Arg.Any<CancellationToken>()).Returns(UserFixtures.MakeUserInfo(_admin, displayName: "Peter"));
        await using var client = await ConnectAsync(AdminKey, ct);

        var result = await client.CallToolAsync("hello", cancellationToken: ct);

        var hello = result.StructuredContent!.Value;
        hello.GetProperty("userId").GetGuid().Should().Be(_admin);
        hello.GetProperty("name").GetString().Should().Be("Peter");
        hello.GetProperty("signedInWith").GetString().Should().Be("API key");
    }

    [HumansFact]
    public async Task ListTools_OwnerOutsideToolScope_HidesTool()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(TasksKey, ct);

        var tools = await client.ListToolsAsync(cancellationToken: ct);

        tools.Should().NotContain(t => t.Name == "hello");
    }

    [HumansFact]
    public async Task CallTool_OwnerOutsideToolScope_Refused()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(TasksKey, ct);

        var call = () => client.CallToolAsync("hello", cancellationToken: ct).AsTask();

        (await call.Should().ThrowAsync<McpProtocolException>()).WithMessage("*You may not use this tool*");
    }

    [HumansFact]
    public async Task ListTools_ToolWithoutScope_HiddenFromAdmin()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(AdminKey, ct, extraTool: true);

        var tools = await client.ListToolsAsync(cancellationToken: ct);

        tools.Select(t => t.Name).Should().Equal("hello");
    }

    [HumansFact]
    public async Task CallTool_ToolWithoutScope_Refused()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(AdminKey, ct, extraTool: true);

        var call = () => client.CallToolAsync("unscoped", cancellationToken: ct).AsTask();

        (await call.Should().ThrowAsync<McpProtocolException>()).WithMessage("*You may not use this tool*");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(string key, CancellationToken ct, bool extraTool = false)
    {
        var baseUri = await StartAsync(ct, extraTool);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(baseUri, Section.McpPath),
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [BackdoorApiKeyHandler.HeaderName] = key },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private async Task<Uri> StartAsync(CancellationToken ct, bool extraTool = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(BrowserSession.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BrowserSession>(BrowserSession.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(_keys).AddSingleton(_users);
        var mcp = Section.AddMcp(builder.Services);
        if (extraTool)
            mcp.WithTools<UnscopedTool>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        Section.MapMcp(_app);
        await _app.StartAsync(ct);
        return new Uri(_app.Urls.Single());
    }

    /// <summary>Stands in for the Identity cookie: every request is signed in as the admin.</summary>
    private sealed class BrowserSession(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Browser";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], SchemeName)),
                SchemeName)));
    }

    [McpServerToolType]
    private sealed class UnscopedTool
    {
        [McpServerTool(Name = "unscoped"), Description("Declares no scope.")]
        public static string Run() => "should never run";
    }
}
