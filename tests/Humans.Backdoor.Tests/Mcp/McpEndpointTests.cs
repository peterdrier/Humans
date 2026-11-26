using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Backdoor.Authentication;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Data;
using Humans.Backdoor.OAuth;
using Humans.Backdoor.Services;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using NodaTime;
using NSubstitute;
using OpenIddict.Server.AspNetCore;

namespace Humans.Backdoor.Tests.Mcp;

/// <summary>
/// A real host with the shipped registration and mapping (<see cref="Section.AddMcp"/>, <see cref="Section.MapMcp"/>),
/// the approval page, a signed-in browser session as the default scheme, and the SDK's own client. Only the OAuth
/// tables are swapped for lists.
/// </summary>
public sealed class McpEndpointTests : IAsyncDisposable
{
    private const string AdminKey = "admin-key";
    private const string TasksKey = "tasks-key";

    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _tasksManager = Guid.NewGuid();
    private readonly IBackdoorApiKeyService _keys = Substitute.For<IBackdoorApiKeyService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly InMemoryOAuthRepository _oauth = new();
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
        hello.GetProperty("server").GetString().Should().StartWith("http://");
        hello.GetProperty("environment").GetString().Should().NotBeNullOrEmpty();
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

    [HumansFact]
    public async Task Post_NoCredentials_PointsToResourceMetadata()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var baseUri = await StartAsync(ct);
        using var http = new HttpClient();

        var response = await PostToolsListAsync(http, baseUri, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.Should().Contain("resource_metadata=");
        var metadata = await http.GetFromJsonAsync<JsonElement>(
            new Uri(baseUri, "/.well-known/oauth-protected-resource"), ct);
        metadata.GetProperty("resource").GetString().Should().Be(new Uri(baseUri, Section.McpPath).ToString());
        metadata.GetProperty("authorization_servers")[0].GetString().Should().StartWith(baseUri.ToString().TrimEnd('/'));
    }

    [HumansFact]
    public async Task SignIn_Approved_HelloSaysSignInWithHumans()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _keys.AdmitsAsync(BrowserSession.UserId, BackdoorScope.AdminAndBoard, Arg.Any<CancellationToken>()).Returns(true);
        var baseUri = await StartAsync(ct);

        var tokens = await SignInAsync(baseUri, ct);
        await using var client = await ConnectWithTokenAsync(baseUri, tokens.AccessToken, ct);
        var result = await client.CallToolAsync("hello", cancellationToken: ct);

        var hello = result.StructuredContent!.Value;
        hello.GetProperty("userId").GetGuid().Should().Be(BrowserSession.UserId);
        hello.GetProperty("signedInWith").GetString().Should().Be("Sign in with Humans");
    }

    [HumansFact]
    public async Task Revoke_Connection_TokensRefused()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var baseUri = await StartAsync(ct);
        var tokens = await SignInAsync(baseUri, ct);

        var connections = _app!.Services.CreateScope().ServiceProvider.GetRequiredService<IBackdoorConnectionService>();
        var connection = (await connections.ListAsync(ct)).Single();
        (await connections.RevokeAsync(connection.Id, BrowserSession.UserId, ct)).Should().BeTrue();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        (await PostToolsListAsync(http, baseUri, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var refresh = await http.PostAsync(new Uri(baseUri, "/" + Section.TokenPath), new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
                ["client_id"] = BackdoorOAuthClients.Claude.ClientId,
            }), ct);
        refresh.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [HumansFact]
    public async Task Post_BothCredentials_Returns401()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var baseUri = await StartAsync(ct);
        var tokens = await SignInAsync(baseUri, ct);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        http.DefaultRequestHeaders.Add(BackdoorApiKeyHandler.HeaderName, AdminKey);

        var response = await PostToolsListAsync(http, baseUri, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [HumansFact]
    public async Task Authorize_PersonNoScopeAdmits_Forbidden()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _keys.IsEligibleAsync(BrowserSession.UserId, Arg.Any<CancellationToken>()).Returns(false);
        var baseUri = await StartAsync(ct);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        var response = await http.GetAsync(AuthorizeUri(baseUri, Challenge(NewVerifier())), ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _oauth.GetConnectionsAsync(ct)).Should().BeEmpty();
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
        builder.Services.AddSingleton<IBackdoorOAuthRepository>(_oauth);
        builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
        builder.Services.AddSingleton(Substitute.For<INotificationEmitter>());
        builder.Services.AddSingleton(Substitute.For<IAuditLogService>());
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(Section).Assembly)
            .ConfigureApplicationPartManager(parts => parts.FeatureProviders.Add(new ApprovalPageOnly()));
        var mcp = Section.AddMcp(builder.Services);
        if (extraTool)
            mcp.WithTools<UnscopedTool>();

        // The test host serves plain HTTP; production sits behind TLS.
        builder.Services.Configure<OpenIddictServerAspNetCoreOptions>(o => o.DisableTransportSecurityRequirement = true);

        _app = builder.Build();
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        Section.MapMcp(_app);
        await _app.StartAsync(ct);
        return new Uri(_app.Urls.Single());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken);

    /// <summary>The Claude app's side of "Sign in with Humans": approval page, Allow, then the code for tokens.</summary>
    private static async Task<Tokens> SignInAsync(Uri baseUri, CancellationToken ct)
    {
        var verifier = NewVerifier();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() });

        var authorize = AuthorizeUri(baseUri, Challenge(verifier));
        var page = await http.GetAsync(authorize, ct);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(ct);
        var antiforgery = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["token"].Value;

        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query)
            .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        form["decision"] = "allow";
        form["__RequestVerificationToken"] = antiforgery;
        var allowed = await http.PostAsync(new Uri(baseUri, "/" + Section.AuthorizePath), new FormUrlEncodedContent(form), ct);
        allowed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        allowed.Headers.Location!.ToString().Should().StartWith(BackdoorOAuthClients.Claude.RedirectUri);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(allowed.Headers.Location.Query)["code"].ToString();

        var exchange = await http.PostAsync(new Uri(baseUri, "/" + Section.TokenPath), new FormUrlEncodedContent(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = BackdoorOAuthClients.Claude.RedirectUri,
                ["client_id"] = BackdoorOAuthClients.Claude.ClientId,
                ["code_verifier"] = verifier,
            }), ct);
        var body = await exchange.Content.ReadFromJsonAsync<JsonElement>(ct);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        return new Tokens(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!);
    }

    private static Uri AuthorizeUri(Uri baseUri, string challenge) =>
        new(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            new Uri(baseUri, "/" + Section.AuthorizePath).ToString(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client_id"] = BackdoorOAuthClients.Claude.ClientId,
                ["redirect_uri"] = BackdoorOAuthClients.Claude.RedirectUri,
                ["response_type"] = "code",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "state-1",
                ["scope"] = "claudeai",
                ["resource"] = new Uri(baseUri, Section.McpPath).ToString(),
            }));

    private static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    private static async Task<McpClient> ConnectWithTokenAsync(Uri baseUri, string accessToken, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(baseUri, Section.McpPath),
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {accessToken}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static Task<HttpResponseMessage> PostToolsListAsync(HttpClient http, Uri baseUri, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, Section.McpPath))
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return http.SendAsync(request, ct);
    }

    /// <summary>Sections' controllers are internal, which MVC skips; the Shell adds them, this adds the one under test.</summary>
    private sealed class ApprovalPageOnly : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(BackdoorOAuthController).GetTypeInfo());
    }

    /// <summary>Stands in for the Identity cookie: every request is signed in as one person.</summary>
    private sealed class BrowserSession(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Browser";

        public static readonly Guid UserId = Guid.NewGuid();

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], SchemeName)),
                SchemeName)));
    }

    [McpServerToolType]
    private sealed class UnscopedTool
    {
        [McpServerTool(Name = "unscoped"), Description("Declares no scope.")]
        public static string Run() => "should never run";
    }
}
