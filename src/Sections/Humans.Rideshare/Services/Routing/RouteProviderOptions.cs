namespace Humans.Rideshare.Services.Routing;

/// <summary>
/// OpenRouteService connection settings. The key comes from the <c>ORS_API_KEY</c>
/// environment variable; the base URL from <c>Rideshare:RouteProvider:BaseUrl</c>.
/// A blank key turns routing off (offers save without geometry).
/// </summary>
internal sealed class RouteProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openrouteservice.org";
}
