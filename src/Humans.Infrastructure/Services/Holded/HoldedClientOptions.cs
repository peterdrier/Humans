namespace Humans.Infrastructure.Services.Holded;

public sealed class HoldedClientOptions
{
    public const string Section = "Holded";

    /// <summary>Bound from the HOLDED_API_KEY env var only — never appsettings.</summary>
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.holded.com";
}
