using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Rideshare.Services.Routing;
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

    [HumansFact]
    public async Task BlankApiKey_ReturnsNullFromBoth_WithoutAnyHttpCall_AndWarnsOnce()
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
            """{"features":[{"geometry":{"coordinates":[2.3522,48.8566]}}]}"""));

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

    [HumansFact]
    public async Task Directions_PostsLngLatPairsWithTheKey_AndReturnsTheGeometry()
    {
        const string geometry = """{"type":"LineString","coordinates":[[2.35,48.85],[-2.4,43.2]]}""";
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
    public async Task Directions_WithFewerThanTwoPoints_ReturnsNull_WithoutCalling()
    {
        var client = Make(Key, _ => throw new InvalidOperationException("must not be called"));

        (await client.GetRouteGeoJsonAsync([TwoPoints[0]], Ct)).Should().BeNull();

        _calls.Should().BeEmpty();
    }

    [HumansFact]
    public async Task NonSuccessResponse_ReturnsNull_AndLogsAWarningWithTheStatus()
    {
        var client = Make(Key, _ => Json(HttpStatusCode.Forbidden, """{"error":"quota exceeded"}"""));

        (await client.GeocodeAsync("Paris", Ct)).Should().BeNull();
        (await client.GetRouteGeoJsonAsync(TwoPoints, Ct)).Should().BeNull();

        _logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(2)
            .And.OnlyContain(e => e.Message.Contains("403") && e.Message.Contains("quota exceeded"));
    }

    [HumansFact]
    public async Task TransportFailure_ReturnsNull_AndLogsAWarning()
    {
        var client = Make(Key, _ => throw new HttpRequestException("connection refused"));

        (await client.GeocodeAsync("Paris", Ct)).Should().BeNull();

        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception is HttpRequestException);
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
