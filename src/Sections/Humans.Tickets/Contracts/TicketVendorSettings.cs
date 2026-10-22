namespace Humans.Tickets.Contracts;

/// <summary>Belongs to the port, not the adapter: deleting the adapter must not take the settings with it.</summary>
public class TicketVendorSettings
{
    public const string SectionName = "TicketVendor";

    public string Provider { get; set; } = "TicketTailor";
    public string EventId { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; } = 15;
    public int BreakEvenTarget { get; set; }

    /// <summary>From the <c>TICKET_VENDOR_API_KEY</c> environment variable, never appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(EventId) && !string.IsNullOrEmpty(ApiKey);
}
