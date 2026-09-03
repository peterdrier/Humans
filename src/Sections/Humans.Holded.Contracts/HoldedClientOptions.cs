namespace Humans.Holded.Contracts;

public sealed class HoldedClientOptions
{
    /// <summary>Bound from the HOLDED_API_KEY_V2 env var only — never appsettings.</summary>
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.holded.com";
}
