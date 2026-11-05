namespace Humans.Users.Models;

internal sealed class BirthdayCalendarViewModel
{
    public List<BirthdayEntryViewModel> Birthdays { get; set; } = [];
    public int CurrentMonth { get; set; }
    public string CurrentMonthName { get; set; } = string.Empty;
}

internal sealed class BirthdayEntryViewModel
{
    public Guid UserId { get; set; }
    public int DayOfMonth { get; set; }
    public List<string> TeamNames { get; set; } = [];
}

internal sealed class MapViewModel
{
    public List<MapMarkerViewModel> Markers { get; set; } = [];
}

internal sealed class MapMarkerViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
}
