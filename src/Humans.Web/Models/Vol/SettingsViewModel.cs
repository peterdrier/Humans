namespace Humans.Web.Models.Vol;

public class SettingsViewModel
{
    public Guid? Id { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public string GateOpeningDate { get; set; } = string.Empty;
    public int BuildStartOffset { get; set; }
    public int EventEndOffset { get; set; }
    public int StrikeEndOffset { get; set; }
    public bool IsShiftBrowsingOpen { get; set; }
    public int? GlobalVolunteerCap { get; set; }
    public int ConfirmedVolunteerCount { get; set; }

    public int BuildDays => -BuildStartOffset;
    public int EventDays => EventEndOffset - 0;
    public int StrikeDays => StrikeEndOffset - EventEndOffset;
}
