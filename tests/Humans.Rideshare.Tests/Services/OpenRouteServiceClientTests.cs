using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Rideshare.Services.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Rideshare.Tests.Services;

/// <summary>
/// <see cref="OpenRouteServiceClient"/> against a stub <see cref="HttpMessageHandler"/>:
/// request shape, response parsing, and the never-throw contract.
/// </summary>
public sealed class OpenRouteServiceClientTests
{
    private const string Key = "test-key";
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static readonly IReadOnlyList<GeoPoint> TwoPoints = [new(48.85, 2.35), new(43.2, -2.4)];

    private readonly List<(HttpRequestMessage Request, string? Body)> _calls = [];
    private readonly CapturingLogger<OpenRouteServiceClient> _logger = new();

    private OpenRouteServiceClient Make(string apiKey, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            _calls.Add((request, body));
            return respond(request);
        });
        return new OpenRouteServiceClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://ors.example/") },
            Options.Create(new RouteProviderOptions { ApiKey = apiKey, BaseUrl = "https://ors.example" }),
            _logger);
    }

    [HumansTheory]
    [Xunit.InlineData("not a URI", false)]
    [Xunit.InlineData("", false)]
    [Xunit.InlineData("/relative", false)]
    [Xunit.InlineData("ftp://ors.example", false)]
    [Xunit.InlineData("https://ors.example/api", true)]
    [Xunit.InlineData("http://ors.example/", true)]
    public async Task Register_ProviderUrl_ResolvesWithoutBlockingRoutingFallback(string baseUrl, bool available)
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Rideshare:RouteProvider:BaseUrl"] = baseUrl }).Build());
        services.Configure<RouteProviderOptions>(o => o.ApiKey = Key);
        services.AddSingleton<ILogger<OpenRouteServiceClient>>(_logger);
        services.AddHttpClient<IRouteProvider, OpenRouteServiceClient>().ConfigurePrimaryHttpMessageHandler(() =>
            new StubHandler(request =>
            {
                _calls.Add((request, null));
                return Task.FromResult(Json(HttpStatusCode.OK, request.Method == HttpMethod.Get
                    ? """{"features":[{"geometry":{"type":"Point","coordinates":[2.35,48.85]}}]}"""
                    : """{"features":[{"geometry":{"type":"LineString","coordinates":[[2.35,48.85],[-2.4,43.2]]}}]}"""));
            }));
        using var provider = services.BuildServiceProvider();

        var routing = provider.GetRequiredService<IRouteProvider>();
        var point = await routing.GeocodeAsync("Paris", Ct);
        var route = await routing.GetRouteGeoJsonAsync(TwoPoints, Ct);

        point.HasValue.Should().Be(available);
        (route is not null).Should().Be(available);
        _calls.Should().HaveCount(available ? 2 : 0);
        if (available)
            _calls.First().Request.RequestUri!.AbsolutePath.Should().Be(
                baseUrl.EndsWith("/api", StringComparison.Ordinal) ? "/api/geocode/search" : "/geocode/search");
        else
            _logger.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Warning);
    }

    [HumansFact]
    public async Task Client_BlankApiKey_ReturnsNullWithoutCall()
    {
        var client = Make("", _ => throw new InvalidOperationException("must not be called"));

        (await client.GeocodeAsync("Paris", Ct)).Should().BeNull();
        (await client.GetRouteGeoJsonAsync(TwoPoints, Ct)).Should().BeNull();

        _calls.Should().BeEmpty();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("ORS_API_KEY"));
    }

    [HumansFact]
    public async Task Geocode_ReadsGeoJsonLngLat_IntoLatLng()
    {
        var client = Make(Key, _ => Json(HttpStatusCode.OK,
            """{"features":[{"geometry":{"type":"Point","coordinates":[2.3522,48.8566]}}]}"""));

        var point = await client.GeocodeAsync("Paris", Ct);

        point.Should().Be(new GeoPoint(48.8566, 2.3522));
        var request = _calls.Should().ContainSingle().Subject.Request;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsolutePath.Should().Be("/geocode/search");
        request.RequestUri.Query.Should().Contain("api_key=test-key").And.Contain("text=Paris").And.Contain("size=1");
    }

    [HumansFact]
    public async Task Geocode_WithNoFeatures_ReturnsNull()
    {
        var client = Make(Key, _ => Json(HttpStatusCode.OK, """{"features":[]}"""));

        (await client.GeocodeAsync("Nowhere in particular", Ct)).Should().BeNull();
    }

    [HumansTheory]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[[2.35,48.85],[-2.4,43.2]]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[[2.35,48.85,100],[-2.4,43.2,200]]}""")]
    public async Task Directions_ValidPoints_ReturnsGeometry(string geometry)
    {
        var client = Make(Key, _ => Json(HttpStatusCode.OK,
            $$"""{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{{geometry}}}]}"""));

        var route = await client.GetRouteGeoJsonAsync(TwoPoints, Ct);

        JsonNormalize(route!).Should().Be(JsonNormalize(geometry));
        var (request, body) = _calls.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/v2/directions/driving-car/geojson");
        request.Headers.GetValues("Authorization").Should().Equal(Key);
        JsonNormalize(body!).Should().Be(JsonNormalize("""{"coordinates":[[2.35,48.85],[-2.4,43.2]]}"""));
    }

    [HumansFact]
    public async Task Directions_FewerThanTwoPoints_ReturnsNull()
    {
        var client = Make(Key, _ => throw new InvalidOperationException("must not be called"));

        (await client.GetRouteGeoJsonAsync([TwoPoints[0]], Ct)).Should().BeNull();

        _calls.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Client_NonSuccessResponse_ReturnsNull()
    {
        var client = Make(Key, _ => Json(HttpStatusCode.Forbidden, """{"error":"quota exceeded"}"""));

        (await client.GeocodeAsync("Paris", Ct)).Should().BeNull();
        (await client.GetRouteGeoJsonAsync(TwoPoints, Ct)).Should().BeNull();

        _logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(2)
            .And.OnlyContain(e => e.Message.Contains("403") && e.Message.Contains("quota exceeded"));
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task Client_RequestFailure_ReturnsNull(bool directions, bool unexpected)
    {
        Exception failure = unexpected ? new NotSupportedException("Unexpected provider fault")
            : new HttpRequestException("connection refused");
        var client = Make(Key, _ => throw failure);

        if (directions)
            (await client.GetRouteGeoJsonAsync(TwoPoints, Ct)).Should().BeNull();
        else
            (await client.GeocodeAsync("Paris", Ct)).Should().BeNull();

        var log = _logger.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(unexpected ? LogLevel.Error : LogLevel.Warning);
        log.Exception.Should().BeSameAs(unexpected ? failure : null);
        if (!unexpected) log.Message.Should().Contain(failure.Message);
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task Cancellation_PropagatesOnlyWhenRequested(bool directions, bool requested)
    {
        using var request = new CancellationTokenSource();
        if (requested) await request.CancelAsync();
        var client = Make(Key, _ => throw new OperationCanceledException("Provider timeout", request.Token));
        Func<Task> read = async () =>
        {
            if (directions)
                (await client.GetRouteGeoJsonAsync(TwoPoints, request.Token)).Should().BeNull();
            else
                (await client.GeocodeAsync("Paris", request.Token)).Should().BeNull();
        };

        if (requested)
        {
            await read.Should().ThrowAsync<OperationCanceledException>();
            _logger.Entries.Should().BeEmpty();
        }
        else
        {
            await read();
            var log = _logger.Entries.Should().ContainSingle().Subject;
            log.Level.Should().Be(LogLevel.Warning);
            log.Exception.Should().BeNull();
        }
    }

    [HumansTheory]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("{}")]
    [Xunit.InlineData("""{"type":"Point","coordinates":[2.35,48.85]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[[2.35,48.85]]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[[2.35],[2.4,43.2]]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[[1e400,48.85],[2.4,43.2]]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[[2.35,91],[2.4,43.2]]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[[181,48],[2,43]]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[["2.35",48.85],[2.4,43.2]]}""")]
    public async Task Directions_InvalidGeometry_ReturnsNull(string geometry)
    {
        var client = Make(Key, _ => Json(HttpStatusCode.OK,
            $$"""{"features":[{"geometry":{{geometry}}}]}"""));
        (await client.GetRouteGeoJsonAsync(TwoPoints, Ct)).Should().BeNull();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception == null);
    }

    [HumansTheory]
    [Xunit.InlineData("""{"coordinates":[2.35,48.85]}""")]
    [Xunit.InlineData("""{"type":"LineString","coordinates":[2.35,48.85]}""")]
    [Xunit.InlineData("""{"type":"Point","coordinates":[1e400,48.85]}""")]
    [Xunit.InlineData("""{"type":"Point","coordinates":[2.35,91]}""")]
    [Xunit.InlineData("""{"type":"Point","coordinates":[181,48.85]}""")]
    public async Task Geocode_InvalidGeometry_ReturnsNull(string geometry)
    {
        var client = Make(Key, _ => Json(HttpStatusCode.OK,
            $$"""{"features":[{"geometry":{{geometry}}}]}"""));
        (await client.GeocodeAsync("Paris", Ct)).Should().BeNull();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception == null);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string JsonNormalize(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
