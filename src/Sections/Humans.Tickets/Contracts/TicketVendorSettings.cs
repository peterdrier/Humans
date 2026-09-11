namespace Humans.Tickets.Contracts;

/// <summary>
/// Configuration for the ticket vendor integration.
/// Non-sensitive values (EventId, Provider, SyncIntervalMinutes, BreakEvenTarget)
/// come from appsettings <c>TicketVendor</c> section. The API key is populated
/// from the <c>TICKET_VENDOR_API_KEY</c> environment variable at DI registration
/// time (Shell's <c>TicketVendorInfrastructureExtensions</c>) and is not stored
/// in appsettings.
/// </summary>
/// <remarks>
/// Belongs to the port, not the adapter: <c>TicketSyncService</c> and
/// <c>TicketVendorHealthCheck</c> read <c>IsConfigured</c> / <c>EventId</c>, and the
/// TicketTailor HTTP client and stub vendor service (<c>Humans.TicketTailor</c>) consume
/// it via the same <c>IOptions&lt;TicketVendorSettings&gt;</c> binding, so deleting the
/// adapter must not take the settings with it.
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
