namespace Humans.Application.Configuration;

/// <summary>
/// Configuration for the ticket vendor integration.
/// Non-sensitive values (EventId, Provider, SyncIntervalMinutes, BreakEvenTarget)
/// come from appsettings <c>TicketVendor</c> section. The API key is populated
/// from the <c>TICKET_VENDOR_API_KEY</c> environment variable at DI registration
/// time (see <c>InfrastructureServiceCollectionExtensions</c>) and is not stored
/// in appsettings.
/// </summary>
/// <remarks>
/// Lives in <c>Humans.Application.Configuration</c> rather than
/// <c>Humans.Infrastructure</c> so the Application-layer
/// <c>TicketSyncService</c> can consume <c>IsConfigured</c> / <c>EventId</c>
/// without reaching into Infrastructure. The TicketTailor HTTP client and stub
/// vendor service (both Infrastructure) consume it via the same
/// <c>IOptions&lt;TicketVendorSettings&gt;</c> binding.
/// </remarks>
public class TicketVendorSettings
{
    public const string SectionName = "TicketVendor";

    public string Provider { get; set; } = "TicketTailor";
    public string EventId { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; } = 15;
    public int BreakEvenTarget { get; set; }

    /// <summary>API key — populated from TICKET_VENDOR_API_KEY env var at DI registration time.
    /// Not stored in appsettings (sensitive). Accessible in settings for testability.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(EventId) && !string.IsNullOrEmpty(ApiKey);
}
