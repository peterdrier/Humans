namespace Humans.Application.Configuration;

/// <summary>
/// Holded API settings. ApiKey comes from env var HOLDED_API_KEY only; never appsettings.
/// Other knobs come from the Holded section in appsettings.json.
/// </summary>
public class HoldedSettings
{
    public const string SectionName = "Holded";

    /// <summary>From env var HOLDED_API_KEY at startup, never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Default daily 04:30 UTC.</summary>
    public string SyncIntervalCron { get; set; } = "0 30 4 * * *";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
