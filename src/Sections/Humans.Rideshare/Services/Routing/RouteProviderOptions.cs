namespace Humans.Rideshare.Services.Routing;

/// <summary>A blank key turns routing off: offers save without geometry.</summary>
internal sealed class RouteProviderOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openrouteservice.org";
}
